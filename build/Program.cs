using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Build.Schema;
using Build.Tasks;
using Build.util;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.Command;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using Cake.Git;
using Json.Schema;
using Json.Schema.Serialization;

namespace Build;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
    public string GameFolderName { get; }
    public int? GameBuildId { get; }
    public string SteamUsername { get; }

    public DirectoryPath RootDirectory { get; }
    public DirectoryPath GameDirectory => RootDirectory.Combine("Games").Combine(GameFolderName);
    public GameVersionEntry TargetVersion => GameMetadata.GameVersions[GameBuildId ?? throw new Exception("Build ID not provided.")];

    public Schema.GameMetadata GameMetadata { get; set; }
    public SteamAppInfo GameAppInfo { get; set; }

    public BuildContext(ICakeContext context) : base(context)
    {
        GameFolderName = context.Argument<string>("game");
        GameBuildId = context.Argument<int?>("build", null);
        SteamUsername = context.Argument<string>("steam-username", "");
        
        RootDirectory = context.Environment.WorkingDirectory.GetParent();
    }

    public GitCommit InferredGitCommit(string message)
    {
        var name  = this.GitConfigGet<string>(RootDirectory, "user.name");
        var email = this.GitConfigGet<string>(RootDirectory, "user.email");

        return this.GitCommit(RootDirectory, name, email, message);
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information("Cleaning up previous build artifacts...");
        context.CleanDirectories(context.RootDirectory.Combine("Games/*/dist").FullPath);
    }
}

[TaskName("RegisterJSONSchemas")]
public sealed class RegisterJsonSchemasTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var dotNetTfmSchema = JsonSchema.FromFile(context.RootDirectory.Combine("assets").Combine("dotnet-target-framework-moniker.schema.json").FullPath);
        SchemaRegistry.Global.Register(dotNetTfmSchema);
        var semVerSchema = JsonSchema.FromFile(context.RootDirectory.Combine("assets").Combine("semver.schema.json").FullPath);
        SchemaRegistry.Global.Register(semVerSchema);
    }
}

[TaskName("Prepare")]
[IsDependentOn(typeof(CleanTask))]
[IsDependentOn(typeof(RegisterJsonSchemasTask))]
public sealed class PrepareTask : AsyncFrostingTask<BuildContext>
{
    public static JsonSerializerOptions GameMetadataSerializerOptions = new()
    {
        Converters =
        {
            new ValidatingJsonConverter(),
        },
        WriteIndented = true,
    };

    public async Task<GameMetadata> DeserializeGameMetadata(BuildContext context)
    {
        if (context.GameDirectory.GetDirectoryName().Equals("Games"))
            throw new ArgumentException("No game folder name provided. Supply one with the '--game [folder name]' switch.");
        
        context.Log.Information("Deserializing game metadata ...");
        await using FileStream gameDataStream = File.OpenRead(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        
        return await JsonSerializer.DeserializeAsync<Schema.GameMetadata>(gameDataStream, GameMetadataSerializerOptions)
            ?? throw new ArgumentException("Game metadata could not be deserialized.");
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.GameMetadata = await DeserializeGameMetadata(context);
        context.Environment.WorkingDirectory = context.GameDirectory;
    }
}

[TaskName("HandleUnknownSteamBuild")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
public sealed class HandleUnknownSteamBuildTask : AsyncFrostingTask<BuildContext>
{
    private async Task SerializeGameMetadata(BuildContext context)
    {
        context.Log.Information("Serializing modified game metadata ...");
        await using FileStream gameDataStream = File.OpenWrite(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        await JsonSerializer.SerializeAsync(
            gameDataStream, 
            context.GameMetadata, 
            new JsonSerializerOptions {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
            }
        );
    }

    private async Task OpenVersionNumberPullRequest(BuildContext context)
    {
        var publicBranchInfo = context.GameAppInfo.Branches["public"];
        if (publicBranchInfo == null) throw new Exception("Current public branch info not found.");
        
        var branchName = $"{context.GameDirectory.GetDirectoryName()}-build-{publicBranchInfo.BuildId}";
        await context.ProcessAsync(
            new CommandSettings
            {
                ToolName = "git",
                ToolExecutableNames = new []{ "git", "git.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("fetch")
                .Append("--prune")
                .Append("origin")
        );
        var branches = context.GitBranches(context.RootDirectory);
        if (branches.Any(branch => branch.FriendlyName == $"origin/{branchName}"))
        {
            throw new Exception("Version entry branch already exists on 'origin', assuming pull request is already open.");
        }

        context.Log.Information("Adding new (partial) version entry to game metadata ...");
        var newVersionEntry = new GameVersionEntry
        {
            BuildId = publicBranchInfo.BuildId,
            TimeUpdated = publicBranchInfo.TimeUpdated,
            GameVersion = "",
            Depots = context.GameMetadata.Steam.DistributionDepots.Select(depotPair => depotPair.Value.DepotId)
                .Select(depotId => context.GameAppInfo.Depots[depotId])
                .Select(depot => new SteamGameDepotVersion {
                    DepotId = depot.DepotId,
                    ManifestId = depot.Manifests["public"].ManifestId,
                })
                .ToDictionary(depotVersion => depotVersion.DepotId),
            FrameworkTargets = context.GameMetadata.GameVersions.Latest()?.FrameworkTargets ?? new List<FrameworkTarget>
            {
                new()
                {
                    TargetFrameworkMoniker = "netstandard2.0",
                    NuGetDependencies = new List<NuGetDependency>()
                },
            },
        };
        context.GameMetadata.GameVersions.Add(newVersionEntry.BuildId, newVersionEntry);
        
        context.Log.Information("Serializing game metadata ...");
        await SerializeGameMetadata(context);
        
        // Remove the partial entry from the deserialized metadata so we can continue to assume the metadata adheres to its JSON schema
        context.Log.Information("Removing new version entry from game metadata ...");
        context.GameMetadata.GameVersions.Remove(newVersionEntry.BuildId);
        
        context.Log.Information("Opening version entry pull request ...");
        context.GitCreateBranch(context.RootDirectory, branchName, true);
        context.GitAdd(context.RootDirectory, context.GameDirectory.CombineWithFilePath("metadata.json"));
        context.InferredGitCommit($"add game version entry for {context.GameAppInfo.Name} build {publicBranchInfo.BuildId}");
        context.Command(
            new CommandSettings
            {
                ToolName = "git",
                ToolExecutableNames = new []{ "git", "git.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("push")
                .Append("--set-upstream")
                .Append("origin")
                .Append(branchName)
        );

        context.Command(
            new CommandSettings
            {
                ToolName = "gh",
                ToolExecutableNames = new[] { "gh", "gh.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("pr")
                .Append("create")
                .AppendSwitch("--title", $"\"[{context.GameDirectory.GetDirectoryName()}] Version entry - Build {publicBranchInfo.BuildId}\"")
                .AppendSwitch(
                    "--body", 
                    $"\"Contains partially patched `metadata.json` for {context.GameAppInfo.Name} build {publicBranchInfo.BuildId}.\n" + 
                    $"Game version number must be populated before merging.\n" + 
                    $"Game version number can likely be inferred from " + 
                    $"[Patchnotes for {context.GameAppInfo.Name} - SteamDB](https://steamdb.info/app/{context.GameMetadata.Steam.AppId}/patchnotes/)\""
                )
                .AppendSwitch("--head", branchName)
        );

        context.Log.Warning("Version number for new build is unknown. Opened pull request to resolve.");
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        var mostRecentKnownVersion = context.GameMetadata.GameVersions.Latest();
        if (mostRecentKnownVersion == null)
        {
            // If there are no known versions, open a pull request.
            await OpenVersionNumberPullRequest(context);
            return;
        }
        
        var currentVersion = context.GameAppInfo.Branches["public"];
        // If the current version is the latest known version, check TimeUpdated matches and do nothing.
        if (currentVersion.BuildId == mostRecentKnownVersion.BuildId)
        {
            if (currentVersion.TimeUpdated != mostRecentKnownVersion.TimeUpdated) 
                context.Log.Warning($"TimeUpdated for most recent known version is inaccurate - Should be {currentVersion.TimeUpdated}");
            
            return;
        }
        
        // If the current version is known, but not the latest known version, warn and do nothing.
        if (context.GameMetadata.GameVersions.Values.Any(version => version.BuildId == currentVersion.BuildId))
        {
            context.Log.Warning("Current version is known, but is not latest?");
            return;
        }
        
        // If the current version is unknown, open a pull request.
        await OpenVersionNumberPullRequest(context);
    }
}

[TaskName("CheckPackageVersionsUpToDate")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
[IsDependentOn(typeof(HandleUnknownSteamBuildTask))]
public sealed class CheckPackageVersionsUpToDateTask : AsyncFrostingTask<BuildContext>
{
    private static HttpClient NuGetClient = new()
    {
        BaseAddress = new Uri("https://api.nuget.org/v3"),
    };

    private async Task<KeyValuePair<GameVersionEntry, bool>> CheckVersionOutdated(BuildContext context, GameVersionEntry versionEntry)
    {
        var packagesExistAtVersion = await Task.WhenAll(
            context.GameMetadata.NuGetPackageNames.Select(
                packageName => NuGetPackageVersionExists(packageName, versionEntry.GameVersion)
            )
        );

        return new KeyValuePair<GameVersionEntry, bool>(versionEntry, !packagesExistAtVersion.All(x => x));
    }
    
    private async Task<GameVersionEntry[]> GetOutdatedVersions(BuildContext context)
    {
        var outdatedStatusPerVersion = await Task.WhenAll(
            context.GameMetadata.GameVersions.Values.Select(
                version => CheckVersionOutdated(context, version)
            )
        );

        var outdatedPackageVersions = outdatedStatusPerVersion
            .Where(pair => pair.Value)
            .Select(pair => pair.Key);

        return outdatedPackageVersions.ToArray();
    }

    private async Task<bool> NuGetPackageVersionExists(string id, string version)
    {
        var result = await NuGetClient.GetAsync($"registration5-semver1/{id.ToLower()}/{version}.json");
        if (result.StatusCode.Equals(HttpStatusCode.NotFound)) return false;
        if (!result.IsSuccessStatusCode) throw new Exception("Failed to check whether NuGet package version exists.");
        return true;
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        var outdatedBuildIds = (await GetOutdatedVersions(context)).Select(version => version.BuildId);
        var outdatedBuildIdsJson = JsonSerializer.Serialize(outdatedBuildIds);
        
        var githubOutputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT", EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(githubOutputFile))
        {
            await using var textWriter = new StreamWriter(githubOutputFile!, true, Encoding.UTF8);
            await textWriter.WriteLineAsync("outdated-version-buildIds<<EOF");
            await textWriter.WriteLineAsync(outdatedBuildIdsJson);
            await textWriter.WriteLineAsync("EOF");
        }
        else
        {
            Console.WriteLine($"::set-output name=outdated-version-buildIds::{outdatedBuildIdsJson}");
        }
    }
}

[TaskName("DownloadNuGetDependencies")]
[IsDependentOn(typeof(PrepareTask))]
public sealed class DownloadNuGetDependenciesTask : AsyncFrostingTask<BuildContext>
{
    private async Task DownloadNuGetPackage(BuildContext context, NuGetDependency package)
    {
        await context.ProcessAsync(
            new CommandSettings
            {
                ToolName = "NuGet",
                ToolExecutableNames = new[] { "nuget", "nuget.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("install")
                .Append(package.Name)
                .AppendSwitch("-Version", package.Version)
                .AppendSwitch("-OutputDirectory", context.GameDirectory.Combine("packages").FullPath)
        );
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.EnsureDirectoryExists(context.GameDirectory.Combine("packages"));
        await Task.WhenAll(
            context.GameMetadata.NuGet.FrameworkTargets
                .SelectMany(target => target.NuGetDependencies)
                .Select(dependency => DownloadNuGetPackage(context, dependency))
        );
    }
}

[TaskName("ListAssembliesFromNuGetDependencies")]
[IsDependentOn(typeof(DownloadNuGetDependenciesTask))]
public sealed class ListAssembliesFromNuGetDependenciesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("FilterAssemblies")]
[IsDependentOn(typeof(SteamDownloadDepotsTask))]
[IsDependentOn(typeof(ListAssembliesFromNuGetDependenciesTask))]
public sealed class FilterAssembliesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("StripAndPubliciseAssemblies")]
[IsDependentOn(typeof(FilterAssembliesTask))]
public sealed class StripAndPubliciseAssembliesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("MakePackage")]
[IsDependentOn(typeof(StripAndPubliciseAssembliesTask))]
public sealed class MakePackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("PublishPackageToNuGet")]
[IsDependentOn(typeof(MakePackageTask))]
public sealed class PushNuGetTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}


[TaskName("Default")]
public class DefaultTask : FrostingTask { }
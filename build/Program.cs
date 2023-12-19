using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Build.Schema;
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
    public string SteamUsername { get; }

    public DirectoryPath RootDirectory { get; }
    public DirectoryPath GameDirectory => RootDirectory.Combine("Games").Combine(GameFolderName);

    public Schema.GameMetadata GameMetadata { get; set; }
    public SteamAppInfo GameAppInfo { get; set; }
    public bool NuGetPackageUpToDate { get; set; }

    public BuildContext(ICakeContext context) : base(context)
    {
        GameFolderName = context.Argument<string>("game", "");
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

[TaskName("CheckPackageUpToDate")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
public sealed class CheckPackageUpToDateTask : AsyncFrostingTask<BuildContext>
{
    private static HttpClient NuGetClient = new()
    {
        BaseAddress = new Uri("https://api.nuget.org/v3"),
    };
    
    private async Task<bool> NuGetPackageUpToDate(BuildContext context)
    {
        var mostRecentKnownVersion = context.GameMetadata.GameVersions.Latest();
        if (mostRecentKnownVersion == null)
        {
            await OpenVersionNumberPullRequest(context);
            return false;
        }
        
        var currentVersion = context.GameAppInfo.Branches["public"];
        if (currentVersion.BuildId != mostRecentKnownVersion.BuildId)
        {
            if (currentVersion.TimeUpdated > mostRecentKnownVersion.TimeUpdated)
            {
                await OpenVersionNumberPullRequest(context);
                return false;
            }

            throw new Exception("Current version differs, but is older than most recent known version?");
        }
        if (currentVersion.TimeUpdated != mostRecentKnownVersion.TimeUpdated) 
            context.Log.Warning($"TimeUpdated for most recent known version is inaccurate - Should be {currentVersion.TimeUpdated}");

        var packagesExist = await Task.WhenAll(
            context.GameMetadata.NuGetPackageNames.Select(
                packageName => NuGetPackageVersionExists(packageName, mostRecentKnownVersion.GameVersion)
            )
        );

        return packagesExist.All(x => x);
    }

    private async Task<bool> NuGetPackageVersionExists(string id, string version)
    {
        var result = await NuGetClient.GetAsync($"registration5-semver1/{id.ToLower()}/{version}.json");
        if (result.StatusCode.Equals(HttpStatusCode.NotFound)) return false;
        if (!result.IsSuccessStatusCode) throw new Exception("Failed to check whether NuGet package version exists.");
        return true;
    }

    private async Task OpenVersionNumberPullRequest(BuildContext context)
    {
        var publicBranchInfo = context.GameAppInfo.Branches["public"];
        if (publicBranchInfo == null) throw new Exception("Current public branch info not found.");

        var newVersionEntry = new GameVersionEntry()
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
        };
        
        context.GameMetadata.GameVersions.Add(newVersionEntry.BuildId, newVersionEntry);
        
        context.Log.Information("Serializing modified game metadata ...");
        await using FileStream gameDataStream = File.OpenWrite(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        await JsonSerializer.SerializeAsync(
            gameDataStream, 
            context.GameMetadata, 
            new JsonSerializerOptions {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }
        );

        var branchName = $"{context.GameDirectory.GetDirectoryName()}-build-{publicBranchInfo.BuildId}";
        context.GitCreateBranch(context.RootDirectory, branchName, true);
        context.GitAdd(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        context.InferredGitCommit($"add game version entry for {context.GameDirectory.GetDirectoryName()} build {publicBranchInfo.BuildId}");
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
                .AppendSwitch("--title", $"Version entry - {context.GameAppInfo.Name} Build {publicBranchInfo.BuildId}")
                .AppendSwitch("--body", "Contains partially patched `metadata.json` for the new version. Game version number must be populated before merging.")
                .AppendSwitch("--head", branchName)
        );
    }

    public override async Task RunAsync(BuildContext context)
    {
        context.NuGetPackageUpToDate = await NuGetPackageUpToDate(context);
    }
}

[TaskName("DownloadDepot")]
[IsDependentOn(typeof(CheckPackageUpToDateTask))]
public sealed class DownloadAssembliesTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;

    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("DownloadNuGetDependencies")]
public sealed class DownloadNuGetDependenciesTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("ListAssembliesFromNuGetDependencies")]
[IsDependentOn(typeof(DownloadNuGetDependenciesTask))]
public sealed class ListAssembliesFromNuGetDependenciesTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("FilterAssemblies")]
[IsDependentOn(typeof(DownloadAssembliesTask))]
[IsDependentOn(typeof(ListAssembliesFromNuGetDependenciesTask))]
public sealed class FilterAssembliesTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("StripAndPubliciseAssemblies")]
[IsDependentOn(typeof(FilterAssembliesTask))]
public sealed class StripAndPubliciseAssembliesTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("MakePackage")]
[IsDependentOn(typeof(StripAndPubliciseAssembliesTask))]
public sealed class MakePackageTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("PublishPackageToNuGet")]
[IsDependentOn(typeof(MakePackageTask))]
public sealed class PushNuGetTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;
    
    public override void Run(BuildContext context)
    {
        
    }
}


[TaskName("Default")]
public class DefaultTask : FrostingTask { }
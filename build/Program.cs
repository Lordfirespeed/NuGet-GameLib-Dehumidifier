using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.AssemblyPublicizer;
using Build.Schema;
using Build.Tasks;
using Build.util;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.Command;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using Cake.Git;
using Json.Schema;
using Json.Schema.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using NuGet;
using HttpClient = System.Net.Http.HttpClient;
using Path = System.IO.Path;

namespace Build;

public static class Program
{
    [STAThread] 
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
    public string NugetApiKey { get; }

    public DirectoryPath RootDirectory { get; }
    public DirectoryPath GameDirectory => RootDirectory.Combine("Games").Combine(GameFolderName);
    public GameVersionEntry TargetVersion => GameMetadata.GameVersions[GameBuildId ?? throw new Exception("Build ID not provided.")];

    public Schema.GameMetadata GameMetadata { get; set; }
    public SteamAppInfo GameAppInfo { get; set; }
    public Dictionary<string, HashSet<string>> FrameworkTargetDependencyAssemblyNames { get; set; }
    public Dictionary<string, Task> AssemblyProcessingTasks { get; set; }

    public BuildContext(ICakeContext context) : base(context)
    {
        GameFolderName = context.Argument<string>("game");
        GameBuildId = context.Argument<int?>("build", null);
        SteamUsername = context.Argument<string>("steam-username", "");
        NugetApiKey = context.Argument<string>("nuget-api-key", "");
        
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
        BaseAddress = new Uri("https://api.nuget.org"),
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
        var result = await NuGetClient.GetAsync($"v3/registration5-gz-semver2/{id.ToLower()}/{version}-alpha.1.json");
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
            context.TargetVersion.FrameworkTargets
                .SelectMany(target => target.NuGetDependencies)
                .Select(dependency => DownloadNuGetPackage(context, dependency))
        );
    }
}

[TaskName("CacheDependencyAssemblyNames")]
[IsDependentOn(typeof(DownloadNuGetDependenciesTask))]
public sealed class CacheDependencyAssemblyNamesTask : FrostingTask<BuildContext>
{
    private delegate bool TargetFrameworkCanConsume(string dependencyTfm);
    
    private HashSet<string> DependencyAssemblyNamesForTfm(BuildContext context, FrameworkTarget target)
    {
        var packageDirs = Directory.GetDirectories(context.GameDirectory.Combine("packages").FullPath)
            .Where(
                folderPath =>
                {
                    var folderName = Path.GetFileName(folderPath);
                    return target.NuGetDependencies.Any(
                        dependency => string.Equals(folderName, $"{dependency.Name}.{dependency.Version}", StringComparison.CurrentCultureIgnoreCase)
                    );
                }
            );
        var sourceDirs = packageDirs.SelectMany(packageDir => new[] { "lib", "ref", "build" }.Select(subDir => Path.Join(packageDir, subDir)))
            .Where(Directory.Exists)
            .ToArray();
        var untargetedAssemblies = sourceDirs.SelectMany(
            sourceDir => Directory.GetFiles(sourceDir, "*.dll")
        );
        var canConsume = ConsumptionChecker(target.TargetFrameworkMoniker);
        var targetedSourceDirs = sourceDirs.SelectMany(Directory.GetDirectories)
            .Where(targetedSourceDir => canConsume(Path.GetFileName(targetedSourceDir)));
        var targetedAssemblies = targetedSourceDirs.SelectMany(
            sourceDir => Directory.GetFiles(sourceDir, "*.dll")
        );

        return targetedAssemblies.Concat(untargetedAssemblies)
            .Select(Path.GetFileName)
            .Where(fileName => !String.IsNullOrWhiteSpace(fileName))
            .ToHashSet()!;
    }

    private TargetFrameworkCanConsume ConsumptionChecker(string tfm)
    {
        var netCoreMatch = NetCoreTfmRegex.Match(tfm);
        if (netCoreMatch.Success)
        {
            var netCoreVersion = new Version(netCoreMatch.Groups[1].Value);
            return dependencyTfm => NetCoreCanConsume(netCoreVersion, dependencyTfm);
        }

        var netFrameworkMatch = NetFrameworkTfmRegex.Match(tfm);
        if (netFrameworkMatch.Success)
        {
            var netFrameworkVersion = int.Parse(netFrameworkMatch.Groups[1].Value);
            return dependencyTfm => NetFrameworkCanConsume(netFrameworkVersion, dependencyTfm);
        }

        var netStandardMatch = NetStandardTfmRegex.Match(tfm);
        if (netStandardMatch.Success)
        {
            var netStandardVersion = new Version(netStandardMatch.Groups[1].Value);
            return dependencyTfm => NetStandardCanConsume(netStandardVersion, dependencyTfm);
        }

        throw new ArgumentException("Unsupported target framework moniker; cannot determine consumable framework versions.");
    }

    private static readonly Regex NetCoreTfmRegex = new(@"^net([5678]\.0)$", RegexOptions.Compiled);
    private static readonly Dictionary<Version, Version> NetCoreNetStandardVersionSupport = new()
    {
        [new Version(8, 0)] = new Version(2, 1),
        [new Version(7, 0)] = new Version(2, 1),
        [new Version(6, 0)] = new Version(2, 1),
        [new Version(5, 0)] = new Version(2, 1),
        [new Version(3, 1)] = new Version(2, 1),
        [new Version(3, 0)] = new Version(2, 1),
        
        [new Version(2, 2)] = new Version(2, 0),
        [new Version(2, 1)] = new Version(2, 0),
        [new Version(2, 0)] = new Version(2, 0),
        
        [new Version(1, 1)] = new Version(1, 6),
        [new Version(1, 0)] = new Version(1, 6),
    };
    private bool NetCoreCanConsume(Version version, string dependencyTfm)
    {
        var netCoreMatch = NetCoreTfmRegex.Match(dependencyTfm);
        if (netCoreMatch.Success)
        {
            return version >= new Version(netCoreMatch.Groups[1].Value);
        }

        if (!NetCoreNetStandardVersionSupport.TryGetValue(version, out var supportedNetStandardVersion))
            return false;

        return NetStandardCanConsume(supportedNetStandardVersion, dependencyTfm);
    }

    private static readonly Regex NetFrameworkTfmRegex = new(@"^net(\d{2,3})$", RegexOptions.Compiled);
    private static readonly int[] NetFrameworkVersionOrder = [11, 20, 35, 40, 403, 45, 451, 452, 46, 461, 462, 47, 471, 472, 48, 481];
    private static readonly Dictionary<int, Version> NetFrameworkNetStandardVersionSupport = new()
    {
        [481] = new Version(2, 0),
        [48] = new Version(2, 0),
        [472] = new Version(2, 0),
        [471] = new Version(2, 0),
        [47] = new Version(2, 0),
        [462] = new Version(2, 0),
        [461] = new Version(2, 0),
        [46] = new Version(1, 3),
        [452] = new Version(1, 2),
        [451] = new Version(1, 2),
        [45] = new Version(1, 1),
    };
    private bool NetFrameworkCanConsume(int version, string dependencyTfm)
    {
        var netFrameworkMatch = NetFrameworkTfmRegex.Match(dependencyTfm);
        if (netFrameworkMatch.Success)
        {
            return Array.IndexOf(NetFrameworkVersionOrder, version) >= 
                   Array.IndexOf(NetFrameworkVersionOrder, int.Parse(netFrameworkMatch.Groups[1].Value));
        }

        if (!NetFrameworkNetStandardVersionSupport.TryGetValue(version, out var supportedNetStandardVersion))
            return false;

        return NetStandardCanConsume(supportedNetStandardVersion, dependencyTfm);
    }

    private static readonly Regex NetStandardTfmRegex = new(@"netstandard([12]\.\d)", RegexOptions.Compiled);
    private bool NetStandardCanConsume(Version version, string dependencyTfm)
    {
        var netStandardMatch = NetStandardTfmRegex.Match(dependencyTfm);
        if (!netStandardMatch.Success) return false;

        return version >= new Version(netStandardMatch.Groups[1].Value);
    }
    
    public override void Run(BuildContext context)
    {
        context.FrameworkTargetDependencyAssemblyNames = new Dictionary<string, HashSet<string>>(
            context.TargetVersion.FrameworkTargets.Select(
                target => new KeyValuePair<string, HashSet<string>>(target.TargetFrameworkMoniker, DependencyAssemblyNamesForTfm(context, target))
            )
        );
    }
}

[TaskName("ProcessAssemblies")]
[IsDependentOn(typeof(SteamDownloadDepotsTask))]
[IsDependentOn(typeof(CacheDependencyAssemblyNamesTask))]
public sealed class ProcessAssembliesTask : AsyncFrostingTask<BuildContext>
{
    private Matcher AssemblyMatcher { get; } = new();
    private Matcher PublicizeMatcher { get; } = new();
    private Dictionary<int, FilePatternMatch[]> DepotAssemblies { get; } = new();
    private readonly object processingTasksLock = new();

    private AssemblyPublicizerOptions StripAndPublicise { get; }= new()
    {
        Target = PublicizeTarget.All,
        Strip = true,
        IncludeOriginalAttributesAttribute = true,
    };
        
    private AssemblyPublicizerOptions StripOnly { get; } = new()
    {
        Target = PublicizeTarget.None,
        Strip = true,
        IncludeOriginalAttributesAttribute = true,
    }; 

    private DirectoryPath DataDirectory(BuildContext context, int depotId)
    {
        var depotDirectory = context.GameDirectory.Combine("steam").Combine($"depot_{depotId}");

        var windowsExe = Directory.EnumerateFiles(depotDirectory.FullPath, "*.exe")
            .FirstOrDefault(filePath => !filePath.StartsWith("UnityCrashHandler"));
        if (windowsExe != null)
        {
            return depotDirectory.Combine($"{Path.GetFileNameWithoutExtension(windowsExe)}_Data");
        }
        
        var linuxExe = Directory
            .EnumerateFiles(depotDirectory.FullPath, "*.x86_64")
            .FirstOrDefault(filePath => !filePath.StartsWith("UnityCrashHandler"));
        if (linuxExe != null)
        {
            return depotDirectory.Combine($"{Path.GetFileNameWithoutExtension(linuxExe)}_Data");
        }
        
        var macOsApp = Directory.EnumerateFiles(depotDirectory.FullPath, "*.app").FirstOrDefault();
        if (macOsApp != null)
        {
            return new DirectoryPath(macOsApp)
                .Combine("Content")
                .Combine("Resources")
                .Combine("Data");
        }

        throw new ArgumentException("Unsupported distribution platform - couldn't find executable/app bundle.");
    }

    private DirectoryPath ManagedDirectory(BuildContext context, int depotId) =>
        DataDirectory(context, depotId).Combine("Managed");
    
    private DirectoryPath DepotTargetNupkgRefsDirectory(BuildContext context, SteamGameDistributionDepot depot, string tfm)
        => context.GameDirectory
            .Combine("nupkgs")
            .Combine($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}")
            .Combine("ref")
            .Combine(tfm);
    
    private async Task ProcessAndCopyAssemblyForDepotTarget(BuildContext context, SteamGameDistributionDepot depot, string tfm, FilePatternMatch fileMatch)
    {
        var filePath = ManagedDirectory(context, depot.DepotId).CombineWithFilePath(fileMatch.Path);
        var fileName = filePath.GetFilename().FullPath;

        if (fileName.EndsWith("-stubs.dll")) return;
        
        var processedFilePath = filePath.GetDirectory()
            .CombineWithFilePath($"{filePath.GetFilenameWithoutExtension()}-stubs.dll");
        
        var dependencyAssemblyNames = context.FrameworkTargetDependencyAssemblyNames[tfm];
        if (dependencyAssemblyNames.Contains(fileName)) return;
        
        bool processingHasStarted;
        Task? processingCompleted;
        TaskCompletionSource? processingCompletedSource = null;
        
        lock (processingTasksLock)
        {
            if (File.Exists(processedFilePath.FullPath))
            {
                context.AssemblyProcessingTasks[filePath.FullPath] = Task.CompletedTask;
            }
            
            processingHasStarted = context.AssemblyProcessingTasks.TryGetValue(filePath.FullPath, out processingCompleted);

            if (!processingHasStarted)
            {
                processingCompletedSource = new TaskCompletionSource();
                processingCompleted = processingCompletedSource.Task;
                context.AssemblyProcessingTasks[filePath.FullPath] = processingCompleted;
            }
        }

        if (!processingHasStarted)
        {
            var shouldPublicise = PublicizeMatcher.Match(fileMatch.Path).HasMatches;
            var options = shouldPublicise ? StripAndPublicise : StripOnly;
            context.Log.Information($"Stripping {(shouldPublicise ? "and publicising " : "")}{depot.DepotId}/{fileName}...");
            AssemblyPublicizer.Publicize(
                filePath.FullPath, 
                processedFilePath.FullPath, 
                options
            );
            processingCompletedSource!.SetResult();
        }

        await (processingCompleted ?? Task.CompletedTask);

        await using FileStream source = File.Open(processedFilePath.FullPath, FileMode.Open);
        await using FileStream destination = File.Create(DepotTargetNupkgRefsDirectory(context, depot, tfm).CombineWithFilePath(fileName).FullPath);
        await source.CopyToAsync(destination);
    }

    private async Task CopyAssembliesForDepotTarget(BuildContext context, SteamGameDistributionDepot depot, string tfm)
    {
        Task ProcessAndCopyAssembly(FilePatternMatch path) => ProcessAndCopyAssemblyForDepotTarget(context, depot, tfm, path);
        context.EnsureDirectoryExists(DepotTargetNupkgRefsDirectory(context, depot, tfm).FullPath);
        
        await Task.WhenAll(
            DepotAssemblies[depot.DepotId]
                .Select(ProcessAndCopyAssembly)
        );
    }
    
    private async Task CopyAssembliesForDepot(BuildContext context, SteamGameDistributionDepot depot)
    {
        DepotAssemblies[depot.DepotId] = AssemblyMatcher.Execute(
            new DirectoryInfoWrapper(new DirectoryInfo(ManagedDirectory(context, depot.DepotId).FullPath))
        ).Files.ToArray();
        
        Task CopyAssembliesForTarget(string tfm) => CopyAssembliesForDepotTarget(context, depot, tfm);
        await Task.WhenAll(
            context.TargetVersion.FrameworkTargets
                .Select(target => target.TargetFrameworkMoniker)
                .Select(CopyAssembliesForTarget)
        );
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.AssemblyProcessingTasks = new();
        
        AssemblyMatcher.AddInclude("*.dll");
        AssemblyMatcher.AddExcludePatterns(context.GameMetadata.ProcessSettings.ExcludeAssemblies);
        
        PublicizeMatcher.AddIncludePatterns(context.GameMetadata.ProcessSettings.AssembliesToPublicise);
        
        await Task.WhenAll(
            context.GameMetadata.Steam.DistributionDepots.Values.Select(
                depot => CopyAssembliesForDepot(context, depot)
            )
        );
    }
} 

[TaskName("MakePackages")]
[IsDependentOn(typeof(ProcessAssembliesTask))]
public sealed class MakePackagesTask : AsyncFrostingTask<BuildContext>
{
    private DirectoryPath DepotNupkgSourceDirectoryPath(BuildContext context, SteamGameDistributionDepot depot)
        => context.GameDirectory
            .Combine("nupkgs")
            .Combine($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}");

    private FilePath DepotNupkgPackedFilePath(BuildContext context, SteamGameDistributionDepot depot)
        => context.GameDirectory
            .Combine("nupkgs")
            .CombineWithFilePath($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}.nupkg");
    
    public async Task MakeDepotPackage(BuildContext context, SteamGameDistributionDepot depot)
    {
        Manifest nuspec = new()
        {
            Metadata = new()
            {
                Authors = "lordfirespeed",
                Version = $"{context.TargetVersion.GameVersion}-alpha.1",
                Id = $"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}",
                Description = context.GameMetadata.NuGet.Description 
                              + "\n\nGenerated and managed by GameLib Dehumidifier.",
                ProjectUrl = "https://github.com/Lordfirespeed/NuGet-GameLib-Dehumidifier",
                DependencySets = context.TargetVersion.FrameworkTargets.Select(
                    target => new ManifestDependencySet
                    {
                        TargetFramework = target.TargetFrameworkMoniker,
                        Dependencies = target.NuGetDependencies.Select(
                            dependency => new ManifestDependency
                            {
                                Id = dependency.Name,
                                Version = dependency.Version,
                            }
                        ).ToList(),
                    }
                ).ToList(),
            },
            Files = [
                new()
                {
                    Source = "ref/**",
                }
            ],
        };

        var builder = new PackageBuilder();
        builder.Populate(nuspec.Metadata);
        builder.PopulateFiles(DepotNupkgSourceDirectoryPath(context, depot).FullPath, nuspec.Files);

        await using FileStream stream = File.Open(DepotNupkgPackedFilePath(context, depot).FullPath, FileMode.OpenOrCreate);
        builder.Save(stream);
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        Func<SteamGameDistributionDepot, Task> makeDepotPackage = depot => MakeDepotPackage(context, depot);
        await Task.WhenAll(
            context.GameMetadata.Steam.DistributionDepots.Values.Select(makeDepotPackage)
        );
    }
}

[TaskName("PushNuGetPackages")]
[IsDependentOn(typeof(MakePackagesTask))]
public sealed class PushNuGetTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var nugetPath = context.GameDirectory.Combine("nupkgs");
        var settings = new DotNetNuGetPushSettings
        {
            Source = "https://api.nuget.org/v3/index.json",
            ApiKey = context.NugetApiKey
        };
        foreach (var pkg in context.GetFiles(nugetPath.Combine("*.nupkg").FullPath))
            context.DotNetNuGetPush(pkg, settings);
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(MakePackagesTask))]
public class DefaultTask : FrostingTask { }
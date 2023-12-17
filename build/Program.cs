using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Build.Schema;
using Cake.Common;
using Cake.Common.IO;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
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
public sealed class CheckPackageUpToDateTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var mostRecentKnownVersion = context.GameMetadata.GameVersions.Latest();
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
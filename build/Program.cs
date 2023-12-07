using System;
using System.IO;
using System.Text.Json;
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
public sealed class PrepareTask : FrostingTask<BuildContext>
{
    public static JsonSerializerOptions GameMetadataSerializerOptions = new JsonSerializerOptions
    {
        Converters = { new ValidatingJsonConverter() }
    };

    public static Schema.GameMetadata DeserializeGameMetadata(BuildContext context)
    {
        if (context.GameDirectory.GetDirectoryName().Equals("Games"))
            throw new ArgumentException("No game folder name provided. Supply one with the '--game [folder name]' switch.");
        
        var gameDataPlain = File.ReadAllText(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        return JsonSerializer.Deserialize<Schema.GameMetadata>(gameDataPlain, GameMetadataSerializerOptions);
    }
    
    public override void Run(BuildContext context)
    {
        context.GameMetadata = DeserializeGameMetadata(context);
        context.Environment.WorkingDirectory = context.GameDirectory;
    }
}

[TaskName("CheckPackageUpToDate")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
public sealed class CheckPackageUpToDateTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("DownloadDepot")]
[IsDependentOn(typeof(CheckPackageUpToDateTask))]
public sealed class DownloadAssembliesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
    }
}

[TaskName("DownloadNuGetDependencies")]
public sealed class DownloadNuGetDependenciesTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        
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
[IsDependentOn(typeof(DownloadAssembliesTask))]
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
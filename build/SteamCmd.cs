using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;

namespace Build;

public abstract class SteamCmdTask : AsyncFrostingTask<BuildContext>
{
    protected async Task<StreamReader> RawSteamCmdOutput(BuildContext context, ProcessArgumentBuilder argumentBuilder)
    {
        argumentBuilder
            .PrependSwitchSecret("+login", context.SteamUsername)
            .Append("+quit");
        
        var startInfo = new ProcessStartInfo
        {
            FileName = "steamcmd",
            Arguments = argumentBuilder.Render(),
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        await process.StandardInput.WriteAsync("\u0004");
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("SteamCMD returned exit code " + process.ExitCode + ".");
        return process.StandardOutput;
    }
}

[TaskName("FetchSteamAppInfo")]
[IsDependentOn(typeof(PrepareTask))]
public class FetchSteamAppInfoTask : SteamCmdTask
{
    protected async Task<SteamAppInfo> SteamCmdAppInfo(BuildContext context, int appId)
    {
        using var rawAppInfoStream = await RawSteamCmdOutput(
            context,
            new ProcessArgumentBuilder()
                .AppendSwitch("+app_info_print", appId.ToString())
        );

        string? currentLine;
        var foundAppInfo = false;
        while ((currentLine = await rawAppInfoStream.ReadLineAsync()) != null)
        {
            if (!currentLine.StartsWith("AppID")) continue;

            foundAppInfo = true;
            break;
        }

        if (!foundAppInfo) throw new Exception("Couldn't find app info in SteamCMD output.");

        return SteamAppInfo.FromVProperty(VdfConvert.Deserialize(await rawAppInfoStream.ReadToEndAsync()));
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.Log.Information("Getting app info from SteamCMD...");
        
        context.GameAppInfo = await SteamCmdAppInfo(context, context.GameMetadata.Steam.AppId);
    }
}

public class SteamAppInfo
{
    public string Name { get; private set; }
    public int LatestBuildId { get; private set; }
    public int TimeUpdated { get; private set; }

    public static SteamAppInfo FromVProperty(VProperty appInfo)
    {
        dynamic appInfoAccess = appInfo;
        dynamic publicBranch = appInfoAccess.Value.depots.branches["public"];

        return new SteamAppInfo
        {
            Name = appInfoAccess.Value.common.name.Value,
            LatestBuildId = Int32.Parse(publicBranch.buildid.Value),
            TimeUpdated = Int32.Parse(publicBranch.timeupdated.Value),
        };
    }

    public override string ToString() => 
        $"SteamAppInfo for {Name}: Latest build {LatestBuildId}, updated at {TimeUpdated}";
}
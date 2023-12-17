using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using ValveKeyValue;

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

        var output = new MemoryStream();
        var outputWriter = new StreamWriter(output);
        outputWriter.AutoFlush = true;

        process.OutputDataReceived += async (sender, args) =>
        {
            await outputWriter.WriteLineAsync(args.Data);
        };
        
        process.Start();
        await process.StandardInput.WriteAsync("\u0004");
        process.BeginOutputReadLine();
        
        await process.WaitForExitAsync();
        await process.StandardInput.DisposeAsync();
        await outputWriter.DisposeAsync();
        
        if (process.ExitCode != 0) throw new Exception("SteamCMD returned exit code " + process.ExitCode + ".");
        return new StreamReader(new MemoryStream(output.GetBuffer()));
    }
}

[TaskName("FetchSteamAppInfo")]
[IsDependentOn(typeof(PrepareTask))]
public class FetchSteamAppInfoTask : SteamCmdTask
{
    protected static readonly KVSerializer VdfSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

    protected async Task<MemoryStream> ExtractAppInfo(StreamReader rawAppInfoStream)
    {
        var writtenAppInfo = new MemoryStream();
        var appInfoWriter = new StreamWriter(writtenAppInfo);
        appInfoWriter.AutoFlush = true;
        
        bool withinAppInfo = false;
        int depth = 0;
        string? currentLine;
        while ((currentLine = await rawAppInfoStream.ReadLineAsync()) != null)
        {
            if (!withinAppInfo && currentLine.StartsWith("AppID"))
            {
                withinAppInfo = true;
                continue;
            }

            if (withinAppInfo)
            {
                await appInfoWriter.WriteLineAsync(currentLine);
            }

            if (withinAppInfo && currentLine.Trim().Equals("{"))
            {
                depth += 1;
                continue;
            }
            
            if (withinAppInfo && currentLine.Trim().Equals("}"))
            {
                depth -= 1;
                if (depth == 0) break;
                continue;
            }
            
        }
        
        var appInfoLength = writtenAppInfo.Length;
        await appInfoWriter.DisposeAsync();
        if (!withinAppInfo) throw new Exception("Couldn't find app info in SteamCMD output.");
        
        Console.WriteLine(await new StreamReader(new MemoryStream(writtenAppInfo.GetBuffer())).ReadToEndAsync());

        var unreadAppInfo = new MemoryStream(writtenAppInfo.GetBuffer());
        unreadAppInfo.SetLength(appInfoLength);
        return unreadAppInfo;
    }
    
    protected async Task<SteamAppInfo> SteamCmdAppInfo(BuildContext context, int appId)
    {
        using var rawAppInfoStream = await RawSteamCmdOutput(
            context,
            new ProcessArgumentBuilder()
                .AppendSwitch("+app_info_print", appId.ToString())
        );

        var appInfo = await ExtractAppInfo(rawAppInfoStream);

        return VdfSerializer.Deserialize<SteamAppInfo>(appInfo);
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.Log.Information("Getting app info from SteamCMD...");
        
        context.GameAppInfo = await SteamCmdAppInfo(context, context.GameMetadata.Steam.AppId);
    }
}

public class SteamAppInfoCommon
{
    [KVProperty("name")]
    public string Name { get; set; }
}

public class SteamAppInfo
{   
    [KVProperty("common")]
    public SteamAppInfoCommon Common { get; set; }
    
    public override string ToString() => 
        $"SteamAppInfo for {Common.Name}";
}
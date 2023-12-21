using System;
using System.IO;
using System.Threading.Tasks;
using Build.util;
using Cake.Common.Tools.Command;
using Cake.Core;
using Cake.Core.IO;
using Cake.Frosting;

namespace Build.Tasks;

public abstract class SteamCmdTaskBase : AsyncFrostingTask<BuildContext>
{
    public delegate ProcessArgumentBuilder BuildArguments(ProcessArgumentBuilder builder);
    
    protected async Task<Tuple<StreamReader?, StreamReader?>> RawSteamCmd(
        BuildContext context, 
        BuildArguments buildArguments,
        bool captureOutput = false,
        bool captureError = false
    )
    {
        var argumentBuilder = buildArguments(
            new ProcessArgumentBuilder()
                .PrependSwitchSecret("+login", context.SteamUsername)
        );
        argumentBuilder
            .Append("+quit");

        return await context.ProcessAsync(
            new CommandSettings
            {
                ToolName = "SteamCMD",
                ToolExecutableNames = new[] { "steamcmd", "steamcmd.exe", },
            },
            argumentBuilder,
            captureOutput: captureOutput,
            captureError: captureError,
            sendToStdin: "\u0004\n"
        );
    }
}

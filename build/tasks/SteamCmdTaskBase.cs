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
    protected async Task<Tuple<StreamReader?, StreamReader?>> RawSteamCmd(
        BuildContext context, 
        ProcessArgumentBuilder argumentBuilder,
        bool captureOutput = false,
        bool captureError = false
    ) {
        argumentBuilder
            .PrependSwitchSecret("+login", context.SteamUsername)
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

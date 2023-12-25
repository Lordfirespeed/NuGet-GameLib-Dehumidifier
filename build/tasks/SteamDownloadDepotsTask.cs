using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Build.Schema;
using Cake.Common.IO;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;

namespace Build.Tasks;

[TaskName("DownloadDepots")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
public class SteamDownloadDepotsTask : SteamCmdTaskBase
{
    private static Regex DownloadCompleteRegex = new(
        """Depot download complete : "(.*)" \(\d+ files, manifest \d+\)""", 
        RegexOptions.Compiled | 
        RegexOptions.IgnoreCase
    );

    private async Task DownloadAndSymlinkDepot(BuildContext context, SteamGameDepotVersion depot)
    {
        var destinationPath = context.GameDirectory.Combine("steam").Combine($"depot_{depot.DepotId}");
        if (context.DirectoryExists(destinationPath))
        {
            context.Log.Information($"Depot {depot.DepotId} is already downloaded, skipping download");
            return;
        }
        
        var (output, _) = await RawSteamCmd(
            context,
            args => args
                .Append("+download_depot")
                .Append(context.GameMetadata.Steam.AppId.ToString())
                .Append(depot.DepotId.ToString())
                .Append(depot.ManifestId.ToString()),
            captureOutput: true
        );
        using var outputManaged = output!;

        Match? downloadCompleteMatch = null;
        while (!outputManaged.EndOfStream)
        {
            var line = await outputManaged.ReadLineAsync();
            if (line == null) continue;
            downloadCompleteMatch = DownloadCompleteRegex.Match(line);
            if (downloadCompleteMatch.Success) break;
        }

        if (downloadCompleteMatch is not { Success: true }) 
            throw new Exception("Couldn't find 'download complete' message in SteamCMD output.");
        var downloadedToPath = new FilePath(downloadCompleteMatch!.Groups[1].Value);
        
        Directory.CreateSymbolicLink(
            destinationPath.FullPath,
            downloadedToPath.FullPath
        );
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.EnsureDirectoryExists(context.GameDirectory.Combine("steam"));
        await Task.WhenAll(
            context.TargetVersion.Depots.Values.Select(
                depot => DownloadAndSymlinkDepot(context, depot)
            )
        );
    }
}
using System.Threading.Tasks;
using Cake.Frosting;

namespace Build.Tasks;

[TaskName("DownloadDepot")]
[IsDependentOn(typeof(CheckPackageUpToDateTask))]
public class SteamDownloadDepotTask : SteamCmdTaskBase
{
    public override bool ShouldRun(BuildContext context) => !context.NuGetPackageUpToDate;

    public override async Task RunAsync(BuildContext context)
    {

    }
}
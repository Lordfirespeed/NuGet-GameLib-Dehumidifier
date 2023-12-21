using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using ValveKeyValue;

namespace Build.Tasks;

[TaskName("FetchSteamAppInfo")]
[IsDependentOn(typeof(PrepareTask))]
public class FetchSteamAppInfoTask : SteamCmdTaskBase
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

        var unreadAppInfo = new MemoryStream(writtenAppInfo.GetBuffer());
        unreadAppInfo.SetLength(appInfoLength);
        return unreadAppInfo;
    }
    
    protected async Task<SteamAppInfo> SteamCmdAppInfo(BuildContext context, int appId)
    {
        var (rawAppInfoStream, _) = await RawSteamCmd(
            context,
            args => args
                .AppendSwitch("+app_info_print", appId.ToString()),
            captureOutput: true
        );
        
        using var rawAppInfoStreamManaged = rawAppInfoStream!;
        var appInfo = await ExtractAppInfo(rawAppInfoStreamManaged);

        var kvAppInfo = VdfSerializer.Deserialize(appInfo);
        return SteamAppInfo.FromKv(kvAppInfo);
    }
    
    public override async Task RunAsync(BuildContext context)
    {
        context.Log.Information("Getting app info from SteamCMD...");
        
        context.GameAppInfo = await SteamCmdAppInfo(context, context.GameMetadata.Steam.AppId);
    }
}

public class SteamAppInfo
{   
    public int AppId { get; set; }
    public string Name { get; set; }
    public Dictionary<int, SteamAppDepot> Depots { get; set; } = new();
    public Dictionary<string, SteamAppBranch> Branches { get; set; } = new();

    public class SteamAppDepot
    {
        public int DepotId { get; set; }
        public Dictionary<string, SteamAppManifest> Manifests { get; set; } = new();

        public static SteamAppDepot FromKv(KVObject kvAppDepot)
        {
            var appDepot = new SteamAppDepot();
            appDepot.PopulateFromKvAppDepot(kvAppDepot);
            return appDepot;
        }

        protected void PopulateFromKvAppDepot(KVObject kvAppDepot)
        {
            DepotId = int.Parse(kvAppDepot.Name);
            foreach (var kvChild in kvAppDepot.Children)
            {
                if (kvChild == null) continue;
                if (kvChild.Name == "manifests")
                {
                    PopulateFromKvAppDepotManifests(kvChild);
                    continue;
                }
            }
        }

        protected void PopulateFromKvAppDepotManifests(KVObject kvAppDepotManifests)
        {
            foreach (var kvChild in kvAppDepotManifests.Children)
            {
                if (kvChild == null) continue;
                Manifests.Add(kvChild.Name, SteamAppManifest.FromKv(kvChild));
            }
        }
    }

    public class SteamAppManifest
    {
        public string BranchName { get; set; }
        public BigInteger ManifestId { get; set; }

        public static SteamAppManifest FromKv(KVObject kvAppManifest)
        {
            var appManifest = new SteamAppManifest();
            appManifest.PopulateFromKvAppManifest(kvAppManifest);
            return appManifest;
        }

        protected void PopulateFromKvAppManifest(KVObject kvAppManifest)
        {
            BranchName = kvAppManifest.Name;
            foreach (var kvChild in kvAppManifest.Children)
            {
                if (kvChild == null) continue;
                if (kvChild.Name == "gid")
                {
                    ManifestId = BigInteger.Parse((string)kvChild.Value);
                    continue;
                }
            }
        }
    }

    public class SteamAppBranch
    {
        public string BranchName { get; set; }
        public int BuildId { get; set; }
        public int TimeUpdated { get; set; }

        public static SteamAppBranch FromKv(KVObject kvAppBranch)
        {
            var appBranch = new SteamAppBranch();
            appBranch.PopulateFromKvAppBranch(kvAppBranch);
            return appBranch;
        }
        
        protected void PopulateFromKvAppBranch(KVObject kvAppBranch)
        {
            BranchName = kvAppBranch.Name;
            foreach (var kvChild in kvAppBranch.Children)
            {
                if (kvChild == null) continue;
                if (kvChild.Name == "buildid")
                {
                    BuildId = int.Parse((string)kvChild.Value);
                    continue;
                }
                if (kvChild.Name == "timeupdated")
                {
                    TimeUpdated = int.Parse((string)kvChild.Value);
                    continue;
                }
            }
        }
    }

    public static SteamAppInfo FromKv(KVObject kvAppInfo)
    {
        var appInfo = new SteamAppInfo();
        appInfo.PopulateFromKvAppInfo(kvAppInfo);
        return appInfo;
    }

    protected void PopulateFromKvAppInfo(KVObject kvAppInfo)
    {
        AppId = int.Parse(kvAppInfo.Name);
        foreach (var kvChild in kvAppInfo.Children)
        {
            if (kvChild == null) continue;
            if (kvChild.Name == "common")
            {
                PopulateFromKvCommon(kvChild);
                continue;
            }
            if (kvChild.Name == "depots")
            {
                PopulateFromKvDepots(kvChild);
                continue;
            }
        }
    }
    
    protected void PopulateFromKvCommon(KVObject kvCommon)
    {
        foreach (var kvChild in kvCommon.Children)
        {
            if (kvChild == null) continue;
            if (kvChild.Name == "name")
            {
                Name = (string)kvChild.Value;
                continue;
            }
        }
    }

    protected void PopulateFromKvDepots(KVObject kvDepots)
    {
        foreach (var kvChild in kvDepots.Children)
        {
            if (kvChild == null) continue;
            if (kvChild.Name == "branches")
            {
                PopulateFromKvBranches(kvChild);
                continue;
            }
            if (int.TryParse(kvChild.Name, CultureInfo.InvariantCulture, out var depotId))
            {
                Depots.Add(depotId, SteamAppDepot.FromKv(kvChild));
                continue;
            }
        }
    }

    protected void PopulateFromKvBranches(KVObject kvBranches)
    {
        foreach (var kvChild in kvBranches.Children)
        {
            if (kvChild == null) continue;
            Branches.Add(kvChild.Name, SteamAppBranch.FromKv(kvChild));
        }
    }
}
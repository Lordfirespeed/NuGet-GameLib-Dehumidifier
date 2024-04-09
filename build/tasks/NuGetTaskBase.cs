using System.Collections.Generic;
using Cake.Frosting;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Build.Tasks;

public abstract class NuGetTaskBase : AsyncFrostingTaskBase<BuildContext>
{
    protected static readonly SourceCacheContext SourceCache = new SourceCacheContext();
    protected static readonly PackageSource Source = new PackageSource("https://api.nuget.org/v3/index.json");
    protected static readonly PackageSource BepInSource = new PackageSource("https://nuget.bepinex.dev/v3/index.json");
    protected static readonly SourceRepository SourceRepository = Repository.Factory.GetCoreV3(Source);
    protected static readonly SourceRepository BepInSourceRepository = Repository.Factory.GetCoreV3(BepInSource);
    protected static readonly IEqualityComparer<IPackageSearchMetadata> PackageSearchMetadataComparer = new PackageSearchMetadataComparerImpl();

    private class PackageSearchMetadataComparerImpl : IEqualityComparer<IPackageSearchMetadata>
    {
        public bool Equals(IPackageSearchMetadata? x, IPackageSearchMetadata? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(y, null)) return false;
            if (x.GetType() != y.GetType()) return false;
            return Equals(x.Identity, y.Identity);
        }

        public int GetHashCode(IPackageSearchMetadata obj)
        {
            return (obj.Identity != null ? obj.Identity.GetHashCode() : 0);
        }
    }
}
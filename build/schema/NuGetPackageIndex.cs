using System;
using System.Text.Json.Serialization;

namespace Build.Schema;

public class NuGetPackageIndex
{
    [JsonPropertyName("items")]
    public NuGetPackageVersionPage[] VersionPages { get; set; }
}

public class NuGetPackageVersionPage
{
    [JsonPropertyName("items")]
    public NuGetPackageVersion[] Versions { get; set; }
}

public class NuGetPackageVersion : IEquatable<NuGetPackageVersion>
{
    public bool Equals(NuGetPackageVersion other)
    {
        return CatalogEntry.Equals(other.CatalogEntry);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((NuGetPackageVersion)obj);
    }

    public override int GetHashCode()
    {
        return CatalogEntry.GetHashCode();
    }

    [JsonPropertyName("catalogEntry")]
    public NuGetCatalogEntry CatalogEntry { get; set; }
}

public class NuGetCatalogEntry: IEquatable<NuGetCatalogEntry>
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("version")]
    public string Version { get; set; }

    public bool Equals(NuGetCatalogEntry? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id && Version == other.Version;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((NuGetCatalogEntry)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Version);
    }
}
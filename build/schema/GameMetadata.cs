using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using Json.Schema;
using Json.Schema.Serialization;

namespace Build.Schema;

[JsonSchema(typeof(GameMetadata), nameof(GameMetadataSchema))]
public class GameMetadata
{
	public static JsonSchema GameMetadataSchema = JsonSchema.FromFile("../assets/game-metadata.schema.json");
	
	[JsonPropertyName("steam")]
	public SteamGameMetadata Steam { get; set; }
	[JsonPropertyName("processSettings")]
	public ProcessSettingsGameMetadata ProcessSettings { get; set; }
	[JsonPropertyName("nuget")]
	public NuGetGameMetadata NuGet { get; set; }
	[JsonPropertyName("gameVersions")]
	[JsonConverter(typeof(GameVersionMapJsonConverter))]
	public GameVersionMap GameVersions { get; set; }

	public IEnumerable<string> NuGetPackageNames => Steam.DistributionDepots.Select(pair => pair.Value)
		.Select(distribution => $"{NuGet.Name}{distribution.PackageSuffix}");
}

public class SteamGameMetadata
{
	[JsonPropertyName("appId")]
	public int AppId { get; set; }
	[JsonPropertyName("gameDistDepots")]
	[JsonConverter(typeof(DistributionDepotMapJsonConverter))]
	public Dictionary<int, SteamGameDistributionDepot> DistributionDepots { get; set; }
}

public class ProcessSettingsGameMetadata
{
	[JsonPropertyName("excludeAssemblies")]
	public List<string> ExcludeAssemblies { get; set; }
	[JsonPropertyName("assembliesToPublicise")]
	public List<string> AssembliesToPublicise { get; set; }
	[JsonPropertyName("isIL2Cpp")]
	public bool IsIl2Cpp { get; set; }
}

public class NuGetGameMetadata
{
	[JsonPropertyName("name")]
	public string Name { get; set; }
	[JsonPropertyName("description")]
	public string Description { get; set; }
	[JsonPropertyName("authors")]
	public List<string> Authors { get; set; }
	[JsonPropertyName("frameworkTargets")]
	public List<FrameworkTarget> FrameworkTargets { get; set; }
}

public class GameVersionMap : Dictionary<int, GameVersionEntry>
{
	public GameVersionEntry? Latest() => Values.Max();
}

public class GameVersionEntry: IComparable<GameVersionEntry>, IEquatable<GameVersionEntry>
{
	[JsonPropertyName("buildId")]
	public int BuildId { get; set; }
	[JsonPropertyName("timeUpdated")]
	public int TimeUpdated { get; set; }
	[JsonPropertyName("gameVersion")]
	public string GameVersion { get; set; }
	[JsonPropertyName("depots")] 
	[JsonConverter(typeof(DepotVersionMapJsonConverter))]
	public Dictionary<int, SteamGameDepotVersion> Depots { get; set; }

	public bool Equals(GameVersionEntry? other)
	{
		if (ReferenceEquals(null, other)) return false;
		if (ReferenceEquals(this, other)) return true;
		return BuildId == other.BuildId;
	}

	public override bool Equals(object? obj)
	{
		if (ReferenceEquals(null, obj)) return false;
		if (ReferenceEquals(this, obj)) return true;
		if (obj.GetType() != this.GetType()) return false;
		return Equals((GameVersionEntry)obj);
	}

	public override int GetHashCode() => BuildId;

	public int CompareTo(GameVersionEntry? other)
	{
		if (ReferenceEquals(this, other)) return 0;
		if (ReferenceEquals(null, other)) return 1;
		return TimeUpdated.CompareTo(other.TimeUpdated);
	}
}

public class FrameworkTarget
{
	[JsonPropertyName("tfm")]
	public string TargetFrameworkMoniker { get; set; }
	[JsonPropertyName("dependencies")]
	public List<NuGetDependency> NuGetDependencies { get; set; }
}

public class NuGetDependency
{
	[JsonPropertyName("name")]
	public string Name { get; set; }
	[JsonPropertyName("version")]
	public string Version { get; set; }
}

public class SteamGameDistributionDepot
{
	[JsonPropertyName("depotId")]
	public int DepotId { get; set; }
	[JsonPropertyName("distributionName")]
	public string DistributionName { get; set; }
	[JsonPropertyName("isDefault")]
	public bool IsDefault { get; set; }
	
	public string PackageSuffix {
		get {
			if (IsDefault) return "";
			return $".{DistributionName}";
		}
	}
}


public class SteamGameDepotVersion
{
	[JsonPropertyName("depotId")]
	public int DepotId { get; set; }
	[JsonPropertyName("manifestId")]
	[JsonConverter(typeof(BigIntegerJsonConverter))]
	public BigInteger ManifestId { get; set; }
}

public class DistributionDepotMapJsonConverter : 
	EntriesDictionaryJsonConverter<Dictionary<int, SteamGameDistributionDepot>, int, SteamGameDistributionDepot>
{
	public override int KeyForValue(SteamGameDistributionDepot value) => value.DepotId;
}

public class GameVersionMapJsonConverter : EntriesDictionaryJsonConverter<GameVersionMap, int, GameVersionEntry>
{
	public override int KeyForValue(GameVersionEntry value) => value.BuildId;
}

public class DepotVersionMapJsonConverter :
	EntriesDictionaryJsonConverter<Dictionary<int, SteamGameDepotVersion>, int, SteamGameDepotVersion>
{
	public override int KeyForValue(SteamGameDepotVersion value) => value.DepotId;
}
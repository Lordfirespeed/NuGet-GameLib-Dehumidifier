using System;
using System.Collections.Generic;
using System.Linq;
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
	public NuGetGameMetadata Nuget { get; set; }
	[JsonPropertyName("steamBuildIdToGameVersionMapping")]
	public BuildIdToGameVersionMapping SteamBuildIdToGameVersionMapping { get; set; }
}

public class SteamGameMetadata
{
	[JsonPropertyName("appId")]
	public int AppId { get; set; }
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

public class BuildIdToGameVersionMapping : List<BuildIdToVersionEntry>
{
	BuildIdToVersionEntry? Latest() => this.MaxBy(x => x.TimeUpdated);
}

public class BuildIdToVersionEntry: IComparable<BuildIdToVersionEntry>, IEquatable<BuildIdToVersionEntry>
{
	[JsonPropertyName("buildId")]
	public int BuildId { get; set; }
	[JsonPropertyName("timeUpdated")]
	public int TimeUpdated { get; set; }
	[JsonPropertyName("gameVersion")]
	public string GameVersion { get; set; }

	public bool Equals(BuildIdToVersionEntry other)
	{
		if (ReferenceEquals(null, other)) return false;
		if (ReferenceEquals(this, other)) return true;
		return BuildId == other.BuildId;
	}

	public override bool Equals(object obj)
	{
		if (ReferenceEquals(null, obj)) return false;
		if (ReferenceEquals(this, obj)) return true;
		if (obj.GetType() != this.GetType()) return false;
		return Equals((BuildIdToVersionEntry)obj);
	}

	public override int GetHashCode()
	{
		return BuildId;
	}

	public int CompareTo(BuildIdToVersionEntry other)
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

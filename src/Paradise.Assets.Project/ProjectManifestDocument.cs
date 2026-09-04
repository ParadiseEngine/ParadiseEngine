using System.Text.Json.Serialization;

using Tomlyn;
using Tomlyn.Serialization;

namespace Paradise.Assets.Project;

/// <summary>
/// The wire shape of <c>project.toml</c>. Every member is nullable and nothing is validated here,
/// so "absent" stays distinguishable from "held the default"; <see cref="ProjectManifest.Parse"/>
/// decides. Keys are pinned with <c>[JsonPropertyName]</c> (Tomlyn honours it) rather than a
/// naming policy that would also touch the user-chosen profile names.
/// </summary>
internal sealed class ProjectManifestDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("schema_version")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("assets")]
    public AssetsSectionDocument? Assets { get; set; }

    [JsonPropertyName("build")]
    public BuildSectionDocument? Build { get; set; }

    [JsonPropertyName("extract")]
    public ExtractSectionDocument? Extract { get; set; }

    /// <summary>Anything Tomlyn could not map. Non-empty is an error: a typo'd key that a lenient read ignored is a setting that never applied.</summary>
    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class AssetsSectionDocument
{
    [JsonPropertyName("ignore")]
    public List<string>? Ignore { get; set; }

    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class ExtractSectionDocument
{
    [JsonPropertyName("directory")]
    public string? Directory { get; set; }

    [JsonPropertyName("static_mesh_component")]
    public string? StaticMeshComponent { get; set; }

    [JsonPropertyName("skinned_mesh_component")]
    public string? SkinnedMeshComponent { get; set; }

    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class BuildSectionDocument
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, BuildProfileDocument>? Profiles { get; set; }

    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class BuildProfileDocument
{
    [JsonPropertyName("document_format")]
    public string? DocumentFormat { get; set; }

    [JsonPropertyName("texture_quality")]
    public string? TextureQuality { get; set; }

    [JsonPropertyName("pack")]
    public bool? Pack { get; set; }

    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

/// <summary>Every read goes through this; Tomlyn's reflection overloads would fail a NativeAOT host at the first manifest read, not at build time.</summary>
[TomlSourceGenerationOptions(DuplicateKeyHandling = TomlDuplicateKeyHandling.Error)]
[TomlSerializable(typeof(ProjectManifestDocument))]
internal sealed partial class ProjectManifestSerializerContext : TomlSerializerContext;

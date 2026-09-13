using System.Text.Json.Serialization;

using Tomlyn;
using Tomlyn.Serialization;

namespace Paradise.Assets.Project;

/// <summary>The unvalidated wire model of <c>project.toml</c>.</summary>
/// <remarks>
/// Nullable members distinguish omitted settings from explicit defaults for <see cref="ProjectManifest.Parse"/>.
/// Explicit JSON names also apply to Tomlyn without transforming user-chosen profile names.
/// </remarks>
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

    [JsonPropertyName("host")]
    public HostSectionDocument? Host { get; set; }

    [JsonPropertyName("extensions")]
    public ExtensionsSectionDocument? Extensions { get; set; }

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

    /// <summary>
    /// Every other key: a kind's directory. Not an error like the other sections' extension data,
    /// because which kinds exist is the extractor chain's to say and the manifest cannot see it —
    /// `verify` reports one nothing declares.
    /// </summary>
    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class ExtensionsSectionDocument
{
    [JsonPropertyName("assemblies")]
    public List<string>? Assemblies { get; set; }

    [JsonPropertyName("projects")]
    public List<string>? Projects { get; set; }

    [TomlExtensionData]
    public Dictionary<string, object?>? Unknown { get; set; }
}

internal sealed class HostSectionDocument
{
    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("arguments")]
    public List<string>? Arguments { get; set; }

    [JsonPropertyName("scene")]
    public string? Scene { get; set; }

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

using System.Text.Json.Serialization;

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

    [JsonPropertyName("build")]
    public BuildSectionDocument? Build { get; set; }
}

internal sealed class BuildSectionDocument
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, BuildProfileDocument>? Profiles { get; set; }
}

internal sealed class BuildProfileDocument
{
    [JsonPropertyName("document_format")]
    public string? DocumentFormat { get; set; }

    [JsonPropertyName("texture_quality")]
    public string? TextureQuality { get; set; }

    [JsonPropertyName("pack")]
    public bool? Pack { get; set; }
}

/// <summary>Every read goes through this; Tomlyn's reflection overloads would fail a NativeAOT host at the first manifest read, not at build time.</summary>
[TomlSerializable(typeof(ProjectManifestDocument))]
internal sealed partial class ProjectManifestSerializerContext : TomlSerializerContext;

using System.Text.Json.Serialization;

using Tomlyn.Serialization;

namespace Paradise.Assets.Project;

/// <summary>
/// The wire shape of <c>assets/project.toml</c>, as Tomlyn deserializes it.
/// </summary>
/// <remarks>
/// <para>
/// Internal and deliberately dumb: every member is nullable and nothing is validated here, so
/// "the key was absent" stays distinguishable from "the key held the default value". The one
/// place that distinction is made is <see cref="ProjectManifest.Parse"/>, which is also the only
/// place that can produce an error message naming the offending profile.
/// </para>
/// <para>
/// TOML keys are pinned with <c>[JsonPropertyName]</c> rather than a naming policy. A policy
/// would also have to be trusted not to rewrite the user-chosen keys of
/// <see cref="BuildSectionDocument.Profiles"/>, and the schema is small enough that spelling it
/// out costs less than that trust.
/// </para>
/// </remarks>
internal sealed class ProjectManifestDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("schema_version")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("build")]
    public BuildSectionDocument? Build { get; set; }
}

/// <summary>The <c>[build]</c> table.</summary>
internal sealed class BuildSectionDocument
{
    /// <summary>
    /// <c>[build.profiles.&lt;name&gt;]</c>. A dictionary because profile names are the game's to
    /// choose — see <see cref="BuildProfile"/>.
    /// </summary>
    [JsonPropertyName("profiles")]
    public Dictionary<string, BuildProfileDocument>? Profiles { get; set; }
}

/// <summary>One <c>[build.profiles.&lt;name&gt;]</c> table.</summary>
internal sealed class BuildProfileDocument
{
    [JsonPropertyName("document_format")]
    public string? DocumentFormat { get; set; }

    [JsonPropertyName("texture_quality")]
    public string? TextureQuality { get; set; }

    [JsonPropertyName("pack")]
    public bool? Pack { get; set; }
}

/// <summary>
/// The source-generated Tomlyn context for the manifest.
/// </summary>
/// <remarks>
/// Every read goes through this. Tomlyn's reflection overloads are never called, which is what
/// lets this assembly claim <c>IsAotCompatible</c> — and what keeps a NativeAOT-published host
/// from failing at the first manifest read rather than at build time.
/// </remarks>
[TomlSerializable(typeof(ProjectManifestDocument))]
internal sealed partial class ProjectManifestSerializerContext : TomlSerializerContext;

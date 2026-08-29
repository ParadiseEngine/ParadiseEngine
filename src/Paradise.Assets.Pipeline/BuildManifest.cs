using System.Text.Json;
using System.Text.Json.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// <c>manifest.json</c> — the machine-readable record of one build: every emitted asset with
/// its output path, source, authoring GUID and content hash.
/// </summary>
/// <remarks>
/// JSON rather than TOML because it is machine-to-machine (the same rule as the schema dump):
/// its consumers are <c>verify</c>, delta packing, and runtime dispatch, never a person with an
/// editor. Paths are forward-slash relative — output paths to the build tree, sources to
/// <c>assets/</c>. This is also where the path→GUID index lives, and the only place: derived,
/// rebuilt on every build, never a source of truth.
/// </remarks>
public sealed class BuildManifest
{
    /// <summary>The manifest format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The manifest's file name inside a build tree.</summary>
    public const string FileName = "manifest.json";

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The project's manifest <c>name</c>.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = "";

    /// <summary>The build profile this tree was compiled with.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    /// <summary>Every emitted asset, in output-path order.</summary>
    [JsonPropertyName("assets")]
    public List<BuiltAsset> Assets { get; set; } = [];

    /// <summary>Serializes this manifest (indented, LF) into <paramref name="path"/>.</summary>
    public void Save(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        Assets.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        var json = JsonSerializer.Serialize(this, BuildManifestJsonContext.Default.BuildManifest);
        fileSystem.WriteAllText(path, json + "\n");
    }
}

/// <summary>One emitted asset.</summary>
public sealed class BuiltAsset
{
    /// <summary>Output path, relative to the build tree.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Source path, relative to <c>assets/</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>The asset's authoring GUID (from its sidecar), or absent for id-less documents.</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    /// <summary>Lowercase hex SHA-256 of the emitted bytes.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Size of the emitted file in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Source-generated STJ context — the pipeline makes the same AOT promise as every other package here.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NewLine = "\n")]
[JsonSerializable(typeof(BuildManifest))]
internal sealed partial class BuildManifestJsonContext : JsonSerializerContext;

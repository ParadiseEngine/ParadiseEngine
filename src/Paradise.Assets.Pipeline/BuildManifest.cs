using System.Text.Json;
using System.Text.Json.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// <c>manifest.json</c> — the derived database of one build: every emitted asset, keyed for
/// lookup by authoring GUID, with its output path, source, and content hash.
/// </summary>
/// <remarks>
/// JSON rather than TOML because it is machine-to-machine (the same rule as the schema dump):
/// its consumers are playmode identity lookup, delta packing, and runtime dispatch, never a
/// person with an editor. Paths are forward-slash relative — output paths to the build tree,
/// sources to <c>assets/</c>.
/// <para>
/// This is the only place a built tree records identity. Source <c>*.meta</c> files stay in
/// <c>assets/</c>; they are not copied next to play or shipped artifacts. A GUID→path/hash
/// map rebuilt every run is what playmode traces a mesh back with, and what a checkout cannot
/// make dirty.
/// </para>
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

    /// <summary>
    /// Identity-bearing assets, keyed by authoring GUID. This is the play/shipped database:
    /// GUID → output path, source, and content hash. Guid-less outputs stay in
    /// <see cref="Assets"/> only.
    /// </summary>
    [JsonPropertyName("byGuid")]
    public Dictionary<string, BuiltAssetIdentity> ByGuid { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Serializes this manifest (indented, LF) into <paramref name="path"/>.</summary>
    public void Save(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        Assets.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        RebuildByGuid();
        var json = JsonSerializer.Serialize(this, BuildManifestJsonContext.Default.BuildManifest);
        fileSystem.WriteAllText(path, json + "\n");
    }

    private void RebuildByGuid()
    {
        var map = new Dictionary<string, BuiltAssetIdentity>(Assets.Count, StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            if (string.IsNullOrEmpty(asset.Guid)) continue;
            map[asset.Guid] = new BuiltAssetIdentity
            {
                Path = asset.Path,
                Source = asset.Source,
                Sha256 = asset.Sha256,
                Size = asset.Size,
            };
        }

        ByGuid = map;
    }

    /// <summary>Reads a previously written manifest.</summary>
    public static BuildManifest Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        var document = JsonSerializer.Deserialize(
            fileSystem.ReadAllText(path), BuildManifestJsonContext.Default.BuildManifest);
        if (document is null) throw new InvalidOperationException($"'{path}' is not a build manifest.");

        document.RebuildByGuid();
        return document;
    }

    /// <summary>The asset recorded under <paramref name="guid"/>, or <see langword="null"/>.</summary>
    public BuiltAsset? FindByGuid(string guid)
    {
        ArgumentException.ThrowIfNullOrEmpty(guid);
        foreach (var asset in Assets)
        {
            if (string.Equals(asset.Guid, guid, StringComparison.Ordinal)) return asset;
        }

        return null;
    }
}

/// <summary>One identity-bearing asset, as stored under its GUID in <see cref="BuildManifest.ByGuid"/>.</summary>
public sealed class BuiltAssetIdentity
{
    /// <summary>Output path, relative to the build tree.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Source path, relative to <c>assets/</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>Lowercase hex SHA-256 of the emitted bytes.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Size of the emitted file in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
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
[JsonSerializable(typeof(Dictionary<string, BuiltAssetIdentity>))]
internal sealed partial class BuildManifestJsonContext : JsonSerializerContext;

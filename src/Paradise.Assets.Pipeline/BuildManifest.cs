using System.Text.Json;
using System.Text.Json.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary><c>manifest.json</c>: the built tree's only record of identity (sidecars stay in <c>assets/</c>). JSON because it is machine-to-machine; paths are forward-slash relative.</summary>
public sealed class BuildManifest
{
    public const int CurrentVersion = 1;

    public const string FileName = "manifest.json";

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("project")]
    public string Project { get; set; } = "";

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    /// <summary>In output-path order.</summary>
    [JsonPropertyName("assets")]
    public List<BuiltAsset> Assets { get; set; } = [];

    /// <summary>Guid-less outputs stay in <see cref="Assets"/> only.</summary>
    [JsonPropertyName("byGuid")]
    public Dictionary<string, BuiltAssetIdentity> ByGuid { get; set; } = new(StringComparer.Ordinal);

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

public sealed class BuiltAssetIdentity
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class BuiltAsset
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>Absent for id-less documents.</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NewLine = "\n")]
[JsonSerializable(typeof(BuildManifest))]
[JsonSerializable(typeof(Dictionary<string, BuiltAssetIdentity>))]
internal sealed partial class BuildManifestJsonContext : JsonSerializerContext;

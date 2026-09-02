using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What the last build into a tree produced, so the next can skip it; derived, never truth.</summary>
/// <remarks>
/// Two tiers because hashing every source is most of the cost being removed; SHA-256 runs only
/// when (mtime, size) fails, so a checkout or re-save still skips. An asset is eligible only when
/// its COMPLETE input is the bytes hashed here (plus its sidecar, whose GUID lands in the
/// manifest): a key that misses an input serves last week's artifact and reports success. So
/// textures (argv + encoder version; <see cref="ArtifactCache"/> keys on those) and prefabs
/// (they bake the prefabs they instance) opt out. Meshes claim eligibility but also depend on
/// their referenced textures existing — issue #201. Anything unrecognised rebuilds.
/// </remarks>
public sealed class BuildIndex
{
    /// <summary>The index format version. A bump invalidates every entry.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The index's file name inside a build tree.</summary>
    public const string FileName = ".build-index.json";

    private readonly Dictionary<string, BuildIndexEntry> _previous;
    private readonly Dictionary<string, BuildIndexEntry> _next = [];
    private readonly string _profile;
    private readonly string _target;

    private BuildIndex(Dictionary<string, BuildIndexEntry> previous, string profile, string target)
    {
        _previous = previous;
        _profile = profile;
        _target = target;
    }

    /// <summary>Reads the index for a tree, or an empty one when it cannot be trusted; a null profile is keyed as <c>""</c>, which no declared profile can be.</summary>
    public static BuildIndex Load(IFileSystem fileSystem, UPath output, string? profile, ProjectOutputTarget target)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        profile ??= "";
        var targetName = target.ToString();
        var path = output / FileName;

        try
        {
            if (fileSystem.FileExists(path))
            {
                var document = JsonSerializer.Deserialize(
                    fileSystem.ReadAllText(path), BuildIndexJsonContext.Default.BuildIndexDocument);

                if (document is { Version: CurrentVersion }
                    && document.Profile == profile
                    && document.Target == targetName)
                {
                    return new BuildIndex(document.Entries, profile, targetName);
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new BuildIndex([], profile, targetName);
    }

    /// <summary>Whether <paramref name="source"/> can be left alone, and what the last build made from it.</summary>
    public bool TryReuse(
        IFileSystem fileSystem,
        UPath source,
        string relative,
        UPath output,
        out IReadOnlyList<BuiltAsset> produced)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        produced = [];

        if (!_previous.TryGetValue(relative, out var entry)) return false;

        var stamp = Stamp(fileSystem, source);
        if (stamp is null) return false;

        var (mtime, size) = stamp.Value;
        var sidecar = SidecarStamp(fileSystem, source);
        if (sidecar != entry.Sidecar) return false;

        if (mtime != entry.Mtime || size != entry.Size)
        {
            if (size != entry.Size || Hash(fileSystem, source) != entry.Sha256) return false;
        }

        // Existence only; a truncated output from a killed build passes — issue #202.
        foreach (var asset in entry.Assets)
        {
            if (!fileSystem.FileExists(output / asset.Path)) return false;
        }

        _next[relative] = entry;
        produced = entry.Assets;
        return true;
    }

    public void Record(IFileSystem fileSystem, UPath source, string relative, IReadOnlyList<BuiltAsset> produced)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var stamp = Stamp(fileSystem, source);
        if (stamp is null) return;

        var (mtime, size) = stamp.Value;
        _next[relative] = new BuildIndexEntry
        {
            Mtime = mtime,
            Size = size,
            Sha256 = Hash(fileSystem, source),
            Sidecar = SidecarStamp(fileSystem, source),
            Assets = [.. produced],
        };
    }

    public void Save(IFileSystem fileSystem, UPath output)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var document = new BuildIndexDocument
        {
            Version = CurrentVersion,
            Profile = _profile,
            Target = _target,
            Entries = _next,
        };

        try
        {
            fileSystem.WriteAllText(
                output / FileName,
                JsonSerializer.Serialize(document, BuildIndexJsonContext.Default.BuildIndexDocument) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Must not fail a build whose output is already complete.
        }
    }

    private static (long Mtime, long Size)? Stamp(IFileSystem fileSystem, UPath path)
    {
        try
        {
            if (!fileSystem.FileExists(path)) return null;
            return (fileSystem.GetLastWriteTime(path).ToUniversalTime().Ticks, fileSystem.GetFileLength(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SidecarStamp(IFileSystem fileSystem, UPath path)
    {
        var sidecar = Documents.SidecarMeta.PathFor(path);
        var stamp = Stamp(fileSystem, sidecar);
        return stamp is { } value ? $"{value.Mtime}:{value.Size}" : "";
    }

    private static string Hash(IFileSystem fileSystem, UPath path)
        => Convert.ToHexStringLower(SHA256.HashData(fileSystem.ReadAllBytes(path)));
}

/// <summary>The on-disk shape of <see cref="BuildIndex"/>.</summary>
public sealed class BuildIndexDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("entries")]
    public Dictionary<string, BuildIndexEntry> Entries { get; set; } = [];
}

/// <summary>One source file, and what the last build made from it.</summary>
public sealed class BuildIndexEntry
{
    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>The asset's <c>*.meta</c> as <c>mtime:size</c>, or <c>""</c> when it has none.</summary>
    [JsonPropertyName("sidecar")]
    public string Sidecar { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<BuiltAsset> Assets { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NewLine = "\n")]
[JsonSerializable(typeof(BuildIndexDocument))]
internal sealed partial class BuildIndexJsonContext : JsonSerializerContext;

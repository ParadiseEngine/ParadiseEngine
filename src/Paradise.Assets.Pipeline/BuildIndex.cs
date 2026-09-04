using System.Text.Json;
using System.Text.Json.Serialization;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What the last build into a tree produced, so the next can skip it; derived, never truth.</summary>
/// <remarks>
/// An entry records every file the importer touched (<see cref="ObservedSources"/>), each with
/// the stamp it had, and reuse means every one of them is unchanged today and every output is
/// still there at its recorded size. Two tiers per input because hashing every source is most of
/// the cost being removed: SHA-256 runs only when (mtime, size) fails, so a checkout or a re-save
/// still skips. Anything the importer does not read through its filesystem — the encoder's
/// version, the manifest's profile table — is folded into the document-wide
/// <see cref="BuildIndexDocument.Environment"/>, and a change there drops the whole index.
/// </remarks>
public sealed class BuildIndex
{
    /// <summary>The index format version. A bump invalidates every entry.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The index's file name inside a build tree.</summary>
    public const string FileName = ".build-index.json";

    private readonly Dictionary<string, BuildIndexEntry> _previous;
    private readonly Dictionary<string, BuildIndexEntry> _next = [];
    private readonly string _profile;
    private readonly string _target;
    private readonly string _environment;

    private BuildIndex(Dictionary<string, BuildIndexEntry> previous, string profile, string target, string environment)
    {
        _previous = previous;
        _profile = profile;
        _target = target;
        _environment = environment;
    }

    /// <summary>Reads the index for a tree, or an empty one when it cannot be trusted; a null profile is keyed as <c>""</c>, which no declared profile can be.</summary>
    /// <param name="environment">Everything an output depends on that no importer reads from disk; the index is dropped when it differs.</param>
    public static BuildIndex Load(IFileSystem fileSystem, UPath output, string? profile, ProjectOutputTarget target, string environment)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(environment);

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
                    && document.Target == targetName
                    && document.Environment == environment)
                {
                    return new BuildIndex(document.Entries, profile, targetName, environment);
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new BuildIndex([], profile, targetName, environment);
    }

    /// <summary>Whether <paramref name="relative"/> can be left alone, and what the last build made from it.</summary>
    public bool TryReuse(
        IFileSystem fileSystem,
        AssetIndex sources,
        string relative,
        UPath output,
        out IReadOnlyList<BuiltAsset> produced)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(sources);
        produced = [];

        if (!_previous.TryGetValue(relative, out var entry)) return false;
        if (entry.Inputs.Count == 0) return false;

        var refreshed = new List<BuildInput>(entry.Inputs.Count);
        foreach (var input in entry.Inputs)
        {
            if (Unchanged(fileSystem, sources, input) is not { } current) return false;
            refreshed.Add(current);
        }

        foreach (var asset in entry.Assets)
        {
            // Size as well as existence: a build killed mid-copy leaves a truncated output that
            // would otherwise be reused forever (issue #202).
            if (FileStamp.Of(fileSystem, output / asset.Path) is not { } stamp || stamp.Size != asset.Size) return false;
        }

        _next[relative] = new BuildIndexEntry { Inputs = refreshed, Assets = entry.Assets };
        produced = entry.Assets;
        return true;
    }

    public void Record(string relative, IReadOnlyList<BuildInput> inputs, IReadOnlyList<BuiltAsset> produced)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(produced);

        _next[relative] = new BuildIndexEntry { Inputs = [.. inputs], Assets = [.. produced] };
    }

    public void Save(IFileSystem fileSystem, UPath output)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var document = new BuildIndexDocument
        {
            Version = CurrentVersion,
            Profile = _profile,
            Target = _target,
            Environment = _environment,
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

    /// <summary>The input as it should be recorded now, or null when the importer would see something different.</summary>
    private static BuildInput? Unchanged(IFileSystem fileSystem, AssetIndex sources, BuildInput input)
    {
        var path = BuildInput.PathOf(sources.Root, input.Path);
        var exists = sources.IsUnderRoot(path) ? sources.Contains(path) : fileSystem.FileExists(path);

        if (input.Kind == BuildInputKind.Presence) return exists == input.Exists ? input : null;

        if (!exists) return null;
        if (FileStamp.Of(fileSystem, path) is not { } stamp) return null;
        if (stamp.Mtime == input.Mtime && stamp.Size == input.Size) return input;
        if (stamp.Size != input.Size) return null;

        try
        {
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fileSystem.ReadAllBytes(path))) != input.Sha256) return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Same bytes under a new stamp (a checkout, a re-save): carry the new stamp so the next
        // build takes the cheap tier.
        return BuildInput.Content(input.Path, stamp, input.Sha256!);
    }
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

    /// <summary>Encoder identity, profile settings, and whatever else shapes output without being read from <c>assets/</c>.</summary>
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "";

    [JsonPropertyName("entries")]
    public Dictionary<string, BuildIndexEntry> Entries { get; set; } = [];
}

/// <summary>One source file: everything its importer read, and what it made.</summary>
public sealed class BuildIndexEntry
{
    [JsonPropertyName("inputs")]
    public List<BuildInput> Inputs { get; set; } = [];

    [JsonPropertyName("assets")]
    public List<BuiltAsset> Assets { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<BuildInputKind>))]
public enum BuildInputKind
{
    /// <summary>The bytes were read; stamp and hash are recorded.</summary>
    Content,

    /// <summary>Only whether the file existed was asked.</summary>
    Presence,
}

/// <summary>One file an importer consulted, with what it saw.</summary>
public sealed class BuildInput
{
    /// <summary>Relative to <c>assets/</c> when under it, else absolute.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public BuildInputKind Kind { get; set; }

    [JsonPropertyName("exists")]
    public bool? Exists { get; set; }

    [JsonPropertyName("mtime")]
    public long? Mtime { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    public static BuildInput Presence(string key, bool exists)
        => new() { Path = key, Kind = BuildInputKind.Presence, Exists = exists };

    public static BuildInput Content(string key, (long Mtime, long Size)? stamp, string sha256)
        => new() { Path = key, Kind = BuildInputKind.Content, Mtime = stamp?.Mtime, Size = stamp?.Size, Sha256 = sha256 };

    internal static string KeyFor(UPath assetsRoot, UPath path)
        => path.IsInDirectory(assetsRoot, recursive: true) ? path.FullName[(assetsRoot.FullName.Length + 1)..] : path.FullName;

    internal static UPath PathOf(UPath assetsRoot, string key)
        => key.StartsWith('/') ? new UPath(key) : assetsRoot / key;
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NewLine = "\n")]
[JsonSerializable(typeof(BuildIndexDocument))]
internal sealed partial class BuildIndexJsonContext : JsonSerializerContext;

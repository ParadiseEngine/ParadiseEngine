using System.Text.Json;
using System.Text.Json.Serialization;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Records inputs and outputs for incremental build reuse.</summary>
/// <remarks>
/// Reuse requires unchanged observed inputs and output content matching the recorded hashes.
/// Input comparison checks (mtime, size) first, then SHA-256 after a stamp change.
/// Non-file inputs, including tool versions and profile settings, belong in
/// <see cref="BuildIndexDocument.Environment"/>; changing it invalidates the whole index.
/// </remarks>
public sealed class BuildIndex
{
    /// <summary>The index format version. A bump invalidates every entry.</summary>
    /// <remarks>3: built materials keep their <c>.material</c> name and built documents spell built paths; an index from 2 would replay outputs under the old names beside new ones (#251). 4: mesh blobs are version 3 and name their skeleton.</remarks>
    public const int CurrentVersion = 4;

    /// <summary>The index's file name inside a build tree.</summary>
    public const string FileName = ".build-index.json";

    private readonly Dictionary<string, BuildIndexEntry> _previous;
    private readonly Dictionary<string, BuildIndexEntry> _next = [];

    // Inputs already checked this build, as they should be recorded; null when one changed. A
    // build asks before verify and again when it walks, and a changed stamp costs a hash of a
    // file that can be tens of megabytes.
    private readonly Dictionary<string, List<BuildInput>?> _checked = new(StringComparer.Ordinal);
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

    internal IReadOnlyDictionary<string, BuildIndexEntry> Entries => _next;

    internal BuildIndex Continue(IReadOnlySet<string> selected)
    {
        var next = new BuildIndex(_next, _profile, _target, _environment);
        foreach (var (source, entry) in _next)
        {
            if (!selected.Contains(source)) next._next.Add(source, entry);
        }

        return next;
    }

    internal IEnumerable<BuiltAsset> PreviousOutputs(IEnumerable<string> selected)
        => selected.Where(_previous.ContainsKey).SelectMany(source => _previous[source].Assets);

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
            if (fileSystem.FileExists(output / BuildManifest.FileName) && fileSystem.FileExists(path))
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

    /// <summary>Whether everything <paramref name="relative"/>'s importer read in the build this index came from reads the same now.</summary>
    /// <remarks>An entry exists only for an asset that built without errors in a build that succeeded, so an unchanged one is an asset the last verify passed on the same inputs.</remarks>
    internal bool InputsUnchanged(IFileSystem fileSystem, AssetIndex sources, string relative)
        => Refreshed(fileSystem, sources, relative) is not null;

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

        if (Refreshed(fileSystem, sources, relative) is not { } refreshed) return false;
        var entry = _previous[relative];

        foreach (var asset in entry.Assets)
        {
            // Size as well as existence: a build killed mid-copy leaves a truncated output that
            // would otherwise be reused forever (issue #202).
            if (FileStamp.Of(fileSystem, output / asset.Path) is not { } stamp || stamp.Size != asset.Size) return false;
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fileSystem.ReadAllBytes(output / asset.Path))) != asset.Sha256) return false;
        }

        _next[relative] = new BuildIndexEntry { Inputs = refreshed, Assets = entry.Assets };
        produced = entry.Assets;
        return true;
    }

    private List<BuildInput>? Refreshed(IFileSystem fileSystem, AssetIndex sources, string relative)
    {
        if (_checked.TryGetValue(relative, out var known)) return known;

        List<BuildInput>? refreshed = null;
        if (_previous.TryGetValue(relative, out var entry) && entry.Inputs.Count > 0)
        {
            refreshed = new List<BuildInput>(entry.Inputs.Count);
            foreach (var input in entry.Inputs)
            {
                if (Unchanged(fileSystem, sources, input) is not { } current)
                {
                    refreshed = null;
                    break;
                }

                refreshed.Add(current);
            }
        }

        _checked[relative] = refreshed;
        return refreshed;
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
    internal static BuildInput? Unchanged(IFileSystem fileSystem, AssetIndex sources, BuildInput input)
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

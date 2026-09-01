using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// What the last build into a tree produced, so the next one can skip the work that would produce
/// it again. Lives inside the output tree, is derived, and is never a source of truth.
/// </summary>
/// <remarks>
/// <para>
/// Two tiers, because hashing every source to discover that none changed is itself most of the
/// cost being removed. <c>(mtime, size)</c> is the cheap gate and answers the common case with no
/// read at all; SHA-256 is consulted only when that gate fails, so a file that was touched but not
/// changed — a <c>git checkout</c>, a re-save — still skips the work.
/// </para>
/// <para>
/// <b>What this may cover is narrower than what it could cover, and the narrowness is the point.</b>
/// The rule is the one the KTX cache is written to: a key that misses an input does not fail, it
/// serves last week's artifact and reports success. So an asset is eligible only when its COMPLETE
/// input is the bytes this index hashes:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Copies — meshes, audio — are eligible.</b> The output is the input, so the source
/// bytes are the whole story.
/// </item>
/// <item>
/// <b>Textures are not.</b> Their output depends on the encode argv and the encoder's version as
/// well as the pixels, and <see cref="ArtifactCache"/> already keys on all of it. Short-circuiting
/// that with a key that knows only the source would survive an encoder upgrade and keep serving
/// images built by the old one.
/// </item>
/// <item>
/// <b>Documents are not.</b> A prefab bakes the prefabs it INSTANCES, so editing
/// <c>box.prefab</c> changes what <c>level.prefab</c> compiles to while leaving the level's own
/// mtime and bytes untouched. Skipping on that key would ship a level missing the edit, silently.
/// Serializing a document is cheap next to copying a megabyte of mesh, so this costs little.
/// </item>
/// </list>
/// <para>
/// An asset's key also covers its <c>*.meta</c>, because the sidecar carries the authoring GUID
/// that lands in the manifest — a sidecar edit changes the output even when the asset did not.
/// </para>
/// <para>
/// Anything unrecognised REBUILDS rather than being trusted: a missing index, a different profile
/// or target, a version bump, an output that is no longer on disk. The index is a cache, and the
/// cost of a wasted rebuild is seconds against a wrong artifact nobody notices.
/// </para>
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

    /// <summary>Reads the index for a tree, or an empty one when it cannot be trusted.</summary>
    /// <param name="profile">
    /// The profile that was named, or <see langword="null"/> for a build that named none and
    /// took <see cref="BuildProfile.Default"/>. Recorded as <c>""</c>, which no declared profile
    /// can collide with — the manifest reader refuses an empty profile name — so an unnamed
    /// build and a declared one never share a key.
    /// </param>
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

                // Profile and target are part of the key, not decoration: one source file compiles
                // to different artifacts under `dev` and `release`, and reusing across them is the
                // silent-wrong-artifact failure this whole class is written to avoid.
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
            // An unreadable index is a cache miss, never an error: the tree it describes is still
            // rebuildable from source, which is the whole contract of everything under .editor/.
        }

        return new BuildIndex([], profile, targetName);
    }

    /// <summary>
    /// Whether <paramref name="source"/> can be left alone, and what the last build made from it.
    /// </summary>
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
            // The cheap gate failed, so pay for the hash before giving up: a checkout or a re-save
            // moves the mtime without changing a byte, and that is common enough to be worth a read.
            if (size != entry.Size || Hash(fileSystem, source) != entry.Sha256) return false;
        }

        // Everything it claims to have produced has to still be there. A hand-deleted output, or a
        // half-finished previous run, must rebuild rather than be reported as present.
        foreach (var asset in entry.Assets)
        {
            if (!fileSystem.FileExists(output / asset.Path)) return false;
        }

        _next[relative] = entry;
        produced = entry.Assets;
        return true;
    }

    /// <summary>Records what one source produced, for the next build to reuse.</summary>
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

    /// <summary>Writes the index describing the build that just finished.</summary>
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
            // Failing to write the index costs the NEXT build its shortcut. It must not fail THIS
            // one, whose output is already complete and correct.
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

    /// <summary>The asset's sidecar as a comparable string, or <c>""</c> when it has none.</summary>
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
    /// <summary>Source last-write time in UTC ticks — the cheap half of the gate.</summary>
    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Lowercase hex SHA-256 of the source bytes.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>The asset's <c>*.meta</c> as <c>mtime:size</c>, or <c>""</c> when it has none.</summary>
    [JsonPropertyName("sidecar")]
    public string Sidecar { get; set; } = "";

    /// <summary>The manifest entries this source produced.</summary>
    [JsonPropertyName("assets")]
    public List<BuiltAsset> Assets { get; set; } = [];
}

/// <summary>Source-generated STJ context — the same AOT promise the rest of the package makes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NewLine = "\n")]
[JsonSerializable(typeof(BuildIndexDocument))]
internal sealed partial class BuildIndexJsonContext : JsonSerializerContext;

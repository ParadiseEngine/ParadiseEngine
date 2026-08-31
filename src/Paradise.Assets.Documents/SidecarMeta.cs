using Zio;

namespace Paradise.Assets.Documents;

/// <summary>
/// The sidecar meta document: one <c>&lt;asset&gt;.meta</c> per asset, carrying the asset's GUID
/// and its per-asset import settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>EVERY asset has one</b> — GLBs, textures and audio banks, whose bytes cannot carry an id,
/// and the project's own text documents too. An earlier design had scenes and configs carry their
/// identity in-file to halve the sidecar count; the saving was not worth what it cost. Identity
/// then had two lookup paths, <c>verify</c> needed two rules for the same question, and a guid
/// key inside a document is a key every reader has to know is structure rather than payload —
/// while a payload is meant to be opaque. One rule for everything is shorter to state, shorter to
/// implement, and leaves nothing to remember.
/// </para>
/// <para>
/// <b>There is no <c>kind</c>.</b> What an asset is, is derived from its path by the classifier —
/// the build always dispatched on the extension, never on the sidecar — so a stored kind was the
/// same fact written twice plus the <c>verify</c> rule policing their agreement. What a sidecar
/// carries instead is what the path CANNOT say: identity, the recorded hash, and import settings.
/// </para>
/// <para>
/// <b>Import settings are open tables, one per domain.</b> Any root table that is not a
/// structural field is a settings domain (<c>[texture]</c> today), preserved opaquely and
/// round-tripped canonically — the same open/strict split as component payloads. The pipeline
/// step that owns a domain interprets and validates it, and <c>verify</c> polices unknown
/// domains there, because the pipeline is where the list of steps lives. The token defaults
/// remain the fallback when a setting is absent.
/// </para>
/// <para>
/// References carry the GUID <b>and</b> the path (<see cref="Paradise.Authoring.AssetReference"/>),
/// so a lost sidecar degrades to a hand-fixable reference rather than breaking every use of its
/// asset — and <c>verify</c> refuses a document where the two halves name different assets.
/// Sidecars are minted and moved by tooling only (<c>mv</c> moves the pair and rewrites the
/// referencing documents); <c>verify</c> fails on orphans and duplicate GUIDs.
/// </para>
/// </remarks>
public sealed class SidecarMeta
{
    /// <summary>The only <c>schema_version</c> this build reads or writes.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The suffix appended to an asset's file name to form its sidecar's.</summary>
    public const string Suffix = ".meta";

    /// <summary>The root keys that are the sidecar's own structure; every other root table is a settings domain.</summary>
    private static readonly string[] s_structuralKeys = ["schema_version", "guid", "hash"];

    private readonly List<KeyValuePair<string, CanonicalTomlTable>> _settings = [];

    /// <summary>Creates a sidecar document.</summary>
    /// <param name="guid">The asset's authoring identity.</param>
    public SidecarMeta(Guid guid)
    {
        Guid = guid;
    }

    /// <summary>The asset's authoring identity.</summary>
    public Guid Guid { get; }

    /// <summary>
    /// SHA-256 of the asset's bytes when the sidecar was written, lowercase hex — or
    /// <see langword="null"/> when the sidecar does not record one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs, neither of which the GUID can do. It tells you an asset changed since its
    /// sidecar was written, which is what a cache needs to know; and it is what lets a LOST
    /// sidecar be re-linked, because content is the only thing left to recognise an asset by once
    /// its id is gone.
    /// </para>
    /// <para>
    /// A mismatch is a <b>warning</b>, never an error. Every legitimate edit to an asset makes
    /// the recorded hash stale, and a rule that turned "someone edited a texture" into a failing
    /// build would be red more often than green — and would train everybody to ignore it.
    /// </para>
    /// <para>
    /// Optional in the format, always written by tooling. A hand-written sidecar with an identity
    /// and no hash is a perfectly good sidecar; it just cannot answer those two questions.
    /// </para>
    /// </remarks>
    public string? Hash { get; set; }

    /// <summary>
    /// The import-settings tables in document order, one per domain. The format carries them
    /// opaquely; the pipeline step owning a domain interprets it.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, CanonicalTomlTable>> Settings => _settings;

    /// <summary>The settings table for <paramref name="domain"/>, or <see langword="null"/>.</summary>
    public CanonicalTomlTable? Setting(string domain)
    {
        foreach (var (candidate, settings) in _settings)
        {
            if (string.Equals(candidate, domain, StringComparison.Ordinal)) return settings;
        }

        return null;
    }

    /// <summary>Adds or replaces the settings table for <paramref name="domain"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="domain"/> is a structural key.</exception>
    public void SetSetting(string domain, CanonicalTomlTable settings)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(settings);
        if (s_structuralKeys.Contains(domain))
        {
            throw new ArgumentException($"'{domain}' is a sidecar field, so it cannot name a settings domain.", nameof(domain));
        }

        var index = _settings.FindIndex(pair => string.Equals(pair.Key, domain, StringComparison.Ordinal));
        var entry = new KeyValuePair<string, CanonicalTomlTable>(domain, settings);
        if (index >= 0) _settings[index] = entry;
        else _settings.Add(entry);
    }

    /// <summary>The hash to record for an asset's bytes.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Reads <paramref name="path"/> and returns the hash of its bytes.</summary>
    public static string ComputeHash(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return ComputeHash(fileSystem.ReadAllBytes(path));
    }

    /// <summary>Mints a sidecar for a new asset: fresh GUID, no settings.</summary>
    public static SidecarMeta Mint() => new(Guid.NewGuid());

    /// <summary>The sidecar path for <paramref name="assetPath"/> (its full name plus <see cref="Suffix"/>).</summary>
    public static UPath PathFor(UPath assetPath)
    {
        assetPath.AssertNotNull(nameof(assetPath));
        return new UPath(assetPath.FullName + Suffix);
    }

    /// <summary>Whether <paramref name="path"/> is a sidecar path.</summary>
    public static bool IsSidecarPath(UPath path) => path.FullName.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>The asset path a sidecar at <paramref name="sidecarPath"/> describes.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecarPath"/> is not a sidecar path.</exception>
    public static UPath AssetPathFor(UPath sidecarPath)
    {
        if (!IsSidecarPath(sidecarPath))
        {
            throw new ArgumentException($"'{sidecarPath}' does not end in '{Suffix}'.", nameof(sidecarPath));
        }

        return new UPath(sidecarPath.FullName[..^Suffix.Length]);
    }

    /// <summary>Reads and validates the sidecar at <paramref name="path"/>.</summary>
    /// <exception cref="SidecarMetaException">The file is unreadable, not TOML, or not a valid sidecar.</exception>
    public static SidecarMeta Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));

        string text;
        try
        {
            text = fileSystem.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SidecarMetaException(path.FullName, $"could not be read ({error.Message})", error);
        }

        return Parse(text, path.FullName);
    }

    /// <summary>Validates an already-read sidecar. The filesystem-free half of <see cref="Load"/>.</summary>
    /// <exception cref="SidecarMetaException">The text is not TOML, or not a valid sidecar.</exception>
    public static SidecarMeta Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new SidecarMetaException(sourceName, problem);

        var root = TomlDocumentReader.Parse(toml, Fail);

        var schemaVersion = TomlDocumentReader.RequireInteger(root, "schema_version", "at the document root", Fail);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw Fail($"declares schema_version = {schemaVersion}, which this build cannot read (supported: {SupportedSchemaVersion})");
        }

        var guidText = TomlDocumentReader.RequireString(root, "guid", "at the document root", Fail);
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty)
        {
            throw Fail($"holds '{guidText}' where 'guid' must be a non-empty UUID");
        }

        var meta = new SidecarMeta(guid);

        if (TomlDocumentReader.OptionalString(root, "hash", "at the document root", Fail) is { } hash)
        {
            // Shape-checked rather than trusted: a truncated or upper-case hash would silently
            // never match, turning the change detector into a permanent false alarm.
            if (hash.Length != 64 || !hash.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                throw Fail($"holds '{hash}' where 'hash' must be a 64-character lowercase hex SHA-256");
            }

            meta.Hash = hash;
        }

        // Everything else is a settings domain, and a domain is a TABLE. A scalar here is either
        // a typo'd structural key or settings written flat; both must fail rather than be dropped
        // by the next machine rewrite.
        foreach (var (key, value) in root)
        {
            if (s_structuralKeys.Contains(key)) continue;
            if (value is not Tomlyn.Model.TomlTable table)
            {
                throw Fail($"has an unknown key '{key}' at the document root — a settings domain is a table");
            }

            meta._settings.Add(new KeyValuePair<string, CanonicalTomlTable>(
                key, TomlDocumentReader.ToCanonical(table, $"in [{key}]", Fail)));
        }

        return meta;
    }

    /// <summary>Renders this sidecar as canonical TOML text.</summary>
    public string Write() => CanonicalTomlWriter.WriteString(ToCanonical());

    /// <summary>Writes this sidecar to <paramref name="path"/> as UTF-8 without BOM.</summary>
    public void Save(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path.AssertNotNull(nameof(path));
        fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(ToCanonical()));
    }

    private CanonicalTomlTable ToCanonical()
    {
        var root = new CanonicalTomlTable
        {
            { "schema_version", (long)SupportedSchemaVersion },
            { "guid", DocumentGuid.Format(Guid) },
        };

        // Identity, then what the asset was, then how to process it -- and model order is what
        // the writer emits, so settings keep their document order.
        if (Hash is { } hash) root.Add("hash", hash);
        foreach (var (domain, settings) in _settings) root.Add(domain, settings);

        return root;
    }
}

/// <summary>A sidecar meta document could not be read, parsed, or validated.</summary>
public sealed class SidecarMetaException : Exception
{
    /// <summary>Creates an exception describing a problem with <paramref name="sourceName"/>.</summary>
    /// <param name="sourceName">The sidecar path, or another name for the source text.</param>
    /// <param name="problem">The problem, phrased to follow the source name.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public SidecarMetaException(string sourceName, string problem, Exception? innerException = null)
        : base($"Sidecar meta '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    /// <summary>The sidecar this failure is about.</summary>
    public string SourceName { get; }
}

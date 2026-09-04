using Zio;

namespace Paradise.Assets.Documents;

/// <summary>One <c>&lt;asset&gt;.meta</c> per asset, text documents included: the asset's GUID and its import settings.</summary>
/// <remarks>
/// No <c>kind</c>: the build dispatches on the extension, so a stored kind would be the same fact
/// twice. Settings domains are opaque here and interpreted by the owning pipeline step, which is
/// where the list of steps lives. Sidecars are minted and moved by tooling only.
/// </remarks>
public sealed class SidecarMeta
{
    public const int SupportedSchemaVersion = 1;

    public const string Suffix = ".meta";

    private static readonly string[] s_structuralKeys = ["schema_version", "guid", "importer"];

    private readonly List<KeyValuePair<string, CanonicalTomlTable>> _settings = [];

    public SidecarMeta(Guid guid)
    {
        Guid = guid;
    }

    public Guid Guid { get; }

    /// <summary>
    /// The importer that handles this asset, by <c>Name</c>: decided by the chain when the sidecar
    /// was minted and honoured as written from then on, so an author can pick a different one for
    /// one asset by editing this line. Null on a sidecar from before the field existed — the
    /// tooling records one on its next pass — never empty.
    /// </summary>
    public string? Importer { get; set; }

    /// <summary>Import-settings tables in document order, one per domain.</summary>
    public IReadOnlyList<KeyValuePair<string, CanonicalTomlTable>> Settings => _settings;

    public CanonicalTomlTable? Setting(string domain)
    {
        foreach (var (candidate, settings) in _settings)
        {
            if (string.Equals(candidate, domain, StringComparison.Ordinal)) return settings;
        }

        return null;
    }

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

    public bool RemoveSetting(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var index = _settings.FindIndex(pair => string.Equals(pair.Key, domain, StringComparison.Ordinal));
        if (index < 0) return false;
        _settings.RemoveAt(index);
        return true;
    }

    public static string ComputeHash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    public static string ComputeHash(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return ComputeHash(fileSystem.ReadAllBytes(path));
    }

    public static SidecarMeta Mint() => new(Guid.NewGuid());

    public static UPath PathFor(UPath assetPath)
    {
        assetPath.AssertNotNull(nameof(assetPath));
        return new UPath(assetPath.FullName + Suffix);
    }

    public static bool IsSidecarPath(UPath path) => path.FullName.EndsWith(Suffix, StringComparison.Ordinal);

    /// <exception cref="ArgumentException"><paramref name="sidecarPath"/> is not a sidecar path.</exception>
    public static UPath AssetPathFor(UPath sidecarPath)
    {
        if (!IsSidecarPath(sidecarPath))
        {
            throw new ArgumentException($"'{sidecarPath}' does not end in '{Suffix}'.", nameof(sidecarPath));
        }

        return new UPath(sidecarPath.FullName[..^Suffix.Length]);
    }

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

    /// <summary>The filesystem-free half of <see cref="Load"/>.</summary>
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

        if (TomlDocumentReader.OptionalString(root, "importer", "at the document root", Fail) is { } importer)
        {
            if (importer.Length == 0) throw Fail("holds an empty 'importer'; name one, or delete the line and let the tooling record it");
            meta.Importer = importer;
        }

        // A scalar here is a typo'd structural key; failing beats the next rewrite dropping it.
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

    public string Write() => CanonicalTomlWriter.WriteString(ToCanonical());

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
        if (Importer is { Length: > 0 } importer) root.Add("importer", importer);

        foreach (var (domain, settings) in _settings) root.Add(domain, settings);

        return root;
    }
}

/// <summary>A sidecar meta document could not be read, parsed, or validated.</summary>
public sealed class SidecarMetaException : Exception
{
    public SidecarMetaException(string sourceName, string problem, Exception? innerException = null)
        : base($"Sidecar meta '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    public string SourceName { get; }
}

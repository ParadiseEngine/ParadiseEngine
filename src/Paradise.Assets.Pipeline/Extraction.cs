using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>How a part is kept in step with the container it came from — the sync policy, which is the engine's, not the format's.</summary>
/// <remarks>
/// A generated prefab has no member here on purpose: it is written once and is the author's from
/// then on, so nothing syncs it and nothing records it. Its <c>prefabs</c> kind still routes it.
/// </remarks>
public enum PartOwnership
{
    /// <summary>Carries no author work, so the pipeline mints and rewrites it freely: the reference documents a build cooks from. No fingerprints — the container is the only side.</summary>
    ToolOwned,

    /// <summary>An authored document whose container side can change under it. Both sides fingerprinted, so a re-export is told from an edit and both at once is a conflict.</summary>
    TwoSided,

    /// <summary>Authored bytes with no document structure — an image the container no longer embeds. Fingerprinted like <see cref="TwoSided"/>, but nothing can be written back.</summary>
    Blob,
}

/// <summary>
/// One file an extraction produced, as the sidecar records it: what kind it is (which decides
/// where it goes), how it is kept in step, and which part of the container it stands for.
/// </summary>
/// <param name="Index">The container's own index for it. That is what a container's slots bind by, and it survives a rename in the DCC, which a name does not.</param>
/// <param name="Name">The readable stem the file was given.</param>
/// <param name="SourceFingerprint">SHA-256 of what the container extracted to at the last sync; null for <see cref="PartOwnership.ToolOwned"/>, which has no second side.</param>
/// <param name="DocumentFingerprint">SHA-256 of the file's comparable half at the last sync.</param>
public sealed record ExtractedPart(
    string Kind,
    PartOwnership Ownership,
    int Index,
    string Name,
    AssetReference Reference,
    string? SourceFingerprint = null,
    string? DocumentFingerprint = null)
{
    /// <summary>The site name <c>verify</c> and <c>refs</c> use for it, so an extracted file is a reference the container holds like any other.</summary>
    public string Where => $"extract.{Kind}[{Index}]";
}

/// <summary>
/// What a container has been extracted to, as its sidecar records it: a flat list of parts, in no
/// format's shape.
/// </summary>
/// <remarks>
/// The engine owns this record rather than each extractor owning a codec for it. Nothing here is
/// glTF's, or any other format's — it is kind, ownership, index, name, identity and the two
/// fingerprints, which is all the pipeline needs to route a part, find it after a move, tell a
/// re-export from an edit, and report it. An extractor that produces something new gets all of
/// that by naming a kind.
/// </remarks>
/// <param name="Directory">The container's own <c>extract</c> override, which outranks the project's per-kind routing for everything it produces.</param>
public sealed record Extraction(string? Directory, IReadOnlyList<ExtractedPart> Parts)
{
    public static Extraction None { get; } = new(null, []);

    /// <summary>Whether the container's tool-owned documents exist. The watcher mints them, so this is only false for one nobody has drained yet.</summary>
    public bool Extracted => Parts.Any(part => part.Ownership == PartOwnership.ToolOwned);

    /// <summary>Whether <c>extract</c> has run: something only it writes is recorded. The watcher's documents alone are not that.</summary>
    public bool Authored => Parts.Any(part => part.Ownership != PartOwnership.ToolOwned);

    /// <summary>The parts of one kind, in container order.</summary>
    public IEnumerable<ExtractedPart> OfKind(string kind) => Parts.Where(part => string.Equals(part.Kind, kind, StringComparison.Ordinal)).OrderBy(part => part.Index);

    /// <summary>The part a container index stands for, or null when this is the first run for it.</summary>
    public ExtractedPart? Find(string kind, int index)
        => Parts.FirstOrDefault(part => string.Equals(part.Kind, kind, StringComparison.Ordinal) && part.Index == index);

    /// <summary>Every recorded part with the site name <c>verify</c> and <c>refs</c> use for it.</summary>
    public IEnumerable<(string Where, AssetReference Reference)> Entries()
        => Parts.Select(part => (part.Where, part.Reference));

    /// <summary>The same record with every part's path half brought up to date through <paramref name="resolve"/>; the input when none moved.</summary>
    public Extraction Repointed(Func<AssetReference, AssetReference?> resolve, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(changes);

        return this with
        {
            Parts = Parts.Select(part =>
            {
                if (resolve(part.Reference) is not { } current || current == part.Reference) return part;
                changes.Add($"{part.Where}: {part.Reference.Path} -> {current.Path}");
                return part with { Reference = current };
            }).ToList(),
        };
    }
}

/// <summary>
/// The <c>[extract]</c> sidecar domain: the one record every extractor's output is written to.
/// </summary>
/// <remarks>
/// A container could carry this itself — a GLB has <c>extras</c> — but an FBX or a USD cannot, and
/// two mechanisms by format is the wrong place to end up. The sidecar is tooling-owned and
/// format-neutral: an extractor only has to say what its container holds, and this records the
/// answer, the way Unity's importer records an FBX's remaps in its <c>.meta</c>. It is derived
/// data the tooling computes from bytes it cannot author, which is why it belongs here and not in
/// a document's own reference list.
/// </remarks>
public sealed class ExtractionRecord : IImportSettingsDomain
{
    public const string Domain = "extract";

    public const string ByKey = "by";
    public const string DirectoryKey = "directory";
    public const string PartsKey = "parts";
    public const string KindKey = "kind";
    public const string OwnershipKey = "ownership";
    public const string IndexKey = "index";
    public const string NameKey = "name";
    public const string SourceKey = "source";
    public const string DocumentKey = "document";

    public static ExtractionRecord Instance { get; } = new();

    private ExtractionRecord()
    {
    }

    /// <inheritdoc />
    public string Name => Domain;

    /// <inheritdoc />
    public string? Problem(CanonicalTomlTable settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var (key, value) in settings)
        {
            switch (key)
            {
                case ByKey or DirectoryKey when value is string: continue;
                case ByKey or DirectoryKey: return $"holds a non-string '{key}' in [{Domain}]";
                case PartsKey when value is IReadOnlyList<object> parts:
                    foreach (var part in parts)
                    {
                        if (ReadPart(part) is null) return $"holds an entry in [{Domain}].{PartsKey} that is not {{ kind, ownership, index, name, guid, path }}";
                    }

                    continue;
                case PartsKey: return $"holds a non-array '{PartsKey}' in [{Domain}]";
                default: return $"holds '{key}' in [{Domain}], which is not an extraction setting";
            }
        }

        return null;
    }

    /// <summary>The name of the extractor that wrote the record, or null when there is none.</summary>
    public static string? WrittenBy(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return meta.Setting(Domain)?.Value(ByKey) as string;
    }

    /// <summary>What the sidecar records, or an empty record for a container nobody has extracted. Malformed parts are skipped: <c>verify</c> names them.</summary>
    public static Extraction Read(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var table = meta.Setting(Domain);
        if (table is null) return Extraction.None;

        var parts = new List<ExtractedPart>();
        if (table.Value(PartsKey) is IReadOnlyList<object> entries)
        {
            foreach (var entry in entries)
            {
                if (ReadPart(entry) is { } part) parts.Add(part);
            }
        }

        return new Extraction(table.Value(DirectoryKey) as string, parts);
    }

    /// <summary>Records <paramref name="extraction"/> under <paramref name="by"/>; the domain goes when there is nothing left in it.</summary>
    public static void Write(SidecarMeta meta, string by, Extraction extraction)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(extraction);

        if (extraction.Directory is null && extraction.Parts.Count == 0)
        {
            meta.RemoveSetting(Domain);
            return;
        }

        var table = new CanonicalTomlTable { { ByKey, by } };
        if (extraction.Directory is { } directory) table.Add(DirectoryKey, directory);
        if (extraction.Parts.Count > 0)
        {
            // Ordered, so a re-extraction that changed nothing produces the same bytes and stays
            // out of the diff: the container's order is index within kind, kinds alphabetical.
            table.Add(PartsKey, extraction.Parts
                .OrderBy(part => part.Kind, StringComparer.Ordinal)
                .ThenBy(part => part.Index)
                .Select(WritePart)
                .Cast<object>()
                .ToList());
        }

        meta.SetSetting(Domain, table);
    }

    private static CanonicalInlineTable WritePart(ExtractedPart part)
    {
        var table = new CanonicalInlineTable
        {
            { KindKey, part.Kind },
            { OwnershipKey, Spell(part.Ownership) },
            { IndexKey, (long)part.Index },
            { NameKey, part.Name },
            { AssetReferenceCodec.GuidKey, DocumentGuid.Format(part.Reference.Guid) },
            { AssetReferenceCodec.PathKey, part.Reference.Path },
        };

        if (part.SourceFingerprint is { } source) table.Add(SourceKey, source);
        if (part.DocumentFingerprint is { } document) table.Add(DocumentKey, document);
        return table;
    }

    private static ExtractedPart? ReadPart(object? value)
    {
        if (value is not (CanonicalTomlTable or CanonicalInlineTable)) return null;
        if (Lookup(value, KindKey) is not string { Length: > 0 } kind) return null;
        if (Ownership(Lookup(value, OwnershipKey) as string) is not { } ownership) return null;
        if (Lookup(value, IndexKey) is not long index || index < 0 || index > int.MaxValue) return null;
        if (Lookup(value, NameKey) is not string name) return null;
        if (Lookup(value, AssetReferenceCodec.GuidKey) is not string guidText || !DocumentGuid.TryParse(guidText, out var guid)) return null;
        if (Lookup(value, AssetReferenceCodec.PathKey) is not string { Length: > 0 } path) return null;

        return new ExtractedPart(
            kind, ownership, (int)index, name, new AssetReference(guid, path),
            Lookup(value, SourceKey) as string,
            Lookup(value, DocumentKey) as string);
    }

    // Spelled rather than round-tripped through the enum name: the sidecar is a file people read
    // and diff, and "two-sided" is what the thing is called everywhere else it is described.
    private static string Spell(PartOwnership ownership) => ownership switch
    {
        PartOwnership.ToolOwned => "tool",
        PartOwnership.TwoSided => "two-sided",
        PartOwnership.Blob => "blob",
        _ => throw new ArgumentOutOfRangeException(nameof(ownership)),
    };

    private static PartOwnership? Ownership(string? spelled) => spelled switch
    {
        "tool" => PartOwnership.ToolOwned,
        "two-sided" => PartOwnership.TwoSided,
        "blob" => PartOwnership.Blob,
        _ => null,
    };

    private static object? Lookup(object? table, string key) => table switch
    {
        CanonicalTomlTable plain => plain.Value(key),
        CanonicalInlineTable inline => inline.Value(key),
        _ => null,
    };
}

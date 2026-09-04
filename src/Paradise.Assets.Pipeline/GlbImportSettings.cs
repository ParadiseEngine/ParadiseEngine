using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>One external file a mesh container names, resolved to an identity.</summary>
/// <param name="Slot">Where in the container the reference sits (<c>images[0]</c>); the key an entry is matched on.</param>
/// <param name="Uri">The uri as the container spells it, relative to itself; recorded so a re-export that changed it can be told from a texture that moved.</param>
/// <param name="Reference">The identity it resolved to, and the assets-relative path that identity lived at.</param>
public readonly record struct MeshReference(string Slot, string Uri, AssetReference Reference);

/// <summary>
/// The <c>[glb]</c> domain: a GLB's external references, resolved to identities and
/// kept in the SIDECAR rather than in the container.
/// </summary>
/// <remarks>
/// A GLB could carry this in <c>extras</c>; an FBX or a USD cannot, and two mechanisms by format
/// is the wrong place to end up. The sidecar is tooling-owned and format-neutral: a per-format
/// reader only has to EXTRACT <c>(slot, uri)</c> pairs, and the pipeline resolves them once and
/// records the answer here — the way Unity's importer records an FBX's texture remaps in its
/// <c>.meta</c>. This is derived data the tooling computes from bytes it cannot author, not a copy
/// of anything authored, which is why it belongs in import settings and a document's reference
/// list does not. It changes only when the container's uris change, so an ordinary edit never
/// dirties it.
/// </remarks>
public sealed class GlbImportSettings : IImportSettingsDomain
{
    public const string Domain = "glb";

    public const string ReferencesKey = "references";

    public const string SlotKey = "slot";

    public const string UriKey = "uri";

    public const string ExtractKey = "extract";
    public const string MeshKey = "mesh";
    public const string SkeletonKey = "skeleton";
    public const string PrefabKey = "prefab";
    public const string ClipsKey = "clips";
    public const string MaterialsKey = "materials";
    public const string NameKey = "name";
    public const string GlbFingerprintKey = "glb";
    public const string DocumentFingerprintKey = "doc";

    public static GlbImportSettings Instance { get; } = new();

    private GlbImportSettings()
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
                case ExtractKey when value is string: continue;
                case ExtractKey: return $"holds a non-string '{ExtractKey}' in [{Domain}]";
                case MeshKey or SkeletonKey or PrefabKey when ReadExtracted(value) is not null: continue;
                case MeshKey or SkeletonKey or PrefabKey: return $"holds '{key}' in [{Domain}] that is not {{ guid, path, glb, doc }}";
                case ClipsKey or MaterialsKey when value is IReadOnlyList<object> named:
                    foreach (var item in named)
                    {
                        if (Lookup(item, NameKey) is not string || ReadExtracted(item) is null)
                        {
                            return $"holds an entry in [{Domain}].{key} that is not {{ name, guid, path, glb, doc }}";
                        }
                    }

                    continue;
                case ClipsKey or MaterialsKey: return $"holds a non-array '{key}' in [{Domain}]";
                case ReferencesKey: break;
                default: return $"holds '{key}' in [{Domain}], which is not a glb setting";
            }

            if (value is not IReadOnlyList<object> entries) return $"holds a non-array '{ReferencesKey}' in [{Domain}]";

            var slots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (ReadEntry(entry) is not { } reference)
                {
                    return $"holds an entry in [{Domain}].{ReferencesKey} that is not {{ slot, uri, guid, path }} with a non-empty UUID";
                }

                if (!slots.Add(reference.Slot))
                {
                    return $"records slot '{reference.Slot}' twice in [{Domain}].{ReferencesKey}; a slot has one identity";
                }
            }
        }

        return null;
    }

    /// <summary>The recorded references, in sidecar order; empty when the domain is absent. Malformed entries are skipped here because <c>verify</c> names them.</summary>
    public static IReadOnlyList<MeshReference> Read(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (meta.Setting(Domain)?.Value(ReferencesKey) is not IReadOnlyList<object> entries) return [];
        var references = new List<MeshReference>();
        foreach (var entry in entries)
        {
            if (ReadEntry(entry) is { } reference) references.Add(reference);
        }

        return references;
    }

    /// <summary>Entries by slot, last wins: a duplicate is verify's finding, not a reason for every other verb to throw.</summary>
    public static Dictionary<string, MeshReference> BySlot(IReadOnlyList<MeshReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var bySlot = new Dictionary<string, MeshReference>(StringComparer.Ordinal);
        foreach (var reference in references) bySlot[reference.Slot] = reference;
        return bySlot;
    }

    /// <summary>Records <paramref name="references"/>, keeping the extraction half of the domain; the domain goes when nothing is left in it.</summary>
    public static void Write(SidecarMeta meta, IReadOnlyList<MeshReference> references)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(references);
        WriteDomain(meta, ReadExtraction(meta), references);
    }

    /// <summary>What <c>extract</c> recorded, or an empty record for a GLB never extracted.</summary>
    public static GlbExtraction ReadExtraction(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var table = meta.Setting(Domain);
        if (table is null) return GlbExtraction.None;

        return new GlbExtraction(
            table.Value(ExtractKey) as string,
            ReadExtracted(table.Value(MeshKey)),
            ReadExtracted(table.Value(SkeletonKey)),
            ReadNamed(table.Value(ClipsKey)),
            ReadNamed(table.Value(MaterialsKey)),
            ReadExtracted(table.Value(PrefabKey)));
    }

    /// <summary>Records <paramref name="extraction"/>, keeping the references half of the domain.</summary>
    public static void WriteExtraction(SidecarMeta meta, GlbExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(extraction);
        WriteDomain(meta, extraction, Read(meta));
    }

    /// <summary>
    /// The one writer of the domain, from parsed values, so the spelling is the same whichever
    /// half changed: the sidecar reader hands an inline table back as a plain one, and copying
    /// that through verbatim wrote it back as a <c>[glb.mesh]</c> section the next run undid.
    /// </summary>
    private static void WriteDomain(SidecarMeta meta, GlbExtraction extraction, IReadOnlyList<MeshReference> references)
    {
        var table = new CanonicalTomlTable();
        if (extraction.Directory is { } directory) table.Add(ExtractKey, directory);
        if (extraction.Mesh is { } mesh) table.Add(MeshKey, WriteEntry(mesh));
        if (extraction.Skeleton is { } skeleton) table.Add(SkeletonKey, WriteEntry(skeleton));
        if (extraction.Prefab is { } prefab) table.Add(PrefabKey, WriteEntry(prefab));
        if (extraction.Clips.Count > 0) table.Add(ClipsKey, extraction.Clips.Select(WriteNamed).Cast<object>().ToList());
        if (extraction.Materials.Count > 0) table.Add(MaterialsKey, extraction.Materials.Select(WriteNamed).Cast<object>().ToList());
        if (references.Count > 0)
        {
            table.Add(ReferencesKey, references.Select(reference => (object)new CanonicalInlineTable
            {
                { SlotKey, reference.Slot },
                { UriKey, reference.Uri },
                { AssetReferenceCodec.GuidKey, DocumentGuid.Format(reference.Reference.Guid) },
                { AssetReferenceCodec.PathKey, reference.Reference.Path },
            }).ToList());
        }

        if (table.Count == 0) meta.RemoveSetting(Domain);
        else meta.SetSetting(Domain, table);
    }

    private static List<GlbExtraction.NamedEntry> ReadNamed(object? value)
    {
        var result = new List<GlbExtraction.NamedEntry>();
        if (value is not IReadOnlyList<object> items) return result;
        foreach (var item in items)
        {
            if (Lookup(item, NameKey) is string name && ReadExtracted(item) is { } entry)
            {
                result.Add(new GlbExtraction.NamedEntry(name, entry));
            }
        }

        return result;
    }

    // A table at a domain's root reads back as a CanonicalTomlTable, one inside an array as a
    // CanonicalInlineTable; the record is the same either way, so both are read here.
    private static GlbExtraction.Entry? ReadExtracted(object? value)
    {
        if (value is not (CanonicalTomlTable or CanonicalInlineTable)) return null;
        if (Lookup(value, AssetReferenceCodec.GuidKey) is not string guidText || !DocumentGuid.TryParse(guidText, out var guid)) return null;
        if (Lookup(value, AssetReferenceCodec.PathKey) is not string { Length: > 0 } path) return null;
        return new GlbExtraction.Entry(
            new AssetReference(guid, path),
            Lookup(value, GlbFingerprintKey) as string ?? "",
            Lookup(value, DocumentFingerprintKey) as string ?? "");
    }

    private static object? Lookup(object? table, string key) => table switch
    {
        CanonicalTomlTable plain => plain.Value(key),
        CanonicalInlineTable inline => inline.Value(key),
        _ => null,
    };

    private static CanonicalInlineTable WriteEntry(GlbExtraction.Entry entry) => new()
    {
        { AssetReferenceCodec.GuidKey, DocumentGuid.Format(entry.Reference.Guid) },
        { AssetReferenceCodec.PathKey, entry.Reference.Path },
        { GlbFingerprintKey, entry.GlbFingerprint },
        { DocumentFingerprintKey, entry.DocumentFingerprint },
    };

    private static CanonicalInlineTable WriteNamed(GlbExtraction.NamedEntry named)
    {
        var table = new CanonicalInlineTable { { NameKey, named.Name } };
        foreach (var (key, value) in WriteEntry(named.Entry)) table.Add(key, value);
        return table;
    }

    private static MeshReference? ReadEntry(object entry)
    {
        if (entry is not CanonicalInlineTable table) return null;
        if (table.Value(SlotKey) is not string { Length: > 0 } slot) return null;
        if (table.Value(UriKey) is not string { Length: > 0 } uri) return null;
        if (table.Value(AssetReferenceCodec.GuidKey) is not string guidText) return null;
        if (table.Value(AssetReferenceCodec.PathKey) is not string { Length: > 0 } path) return null;
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty) return null;
        return new MeshReference(slot, uri, new AssetReference(guid, path));
    }
}

/// <summary>What a GLB has been extracted to, as its sidecar records it: each entry is the document and the two fingerprints of the last sync.</summary>
public sealed record GlbExtraction(
    string? Directory,
    GlbExtraction.Entry? Mesh,
    GlbExtraction.Entry? Skeleton,
    IReadOnlyList<GlbExtraction.NamedEntry> Clips,
    IReadOnlyList<GlbExtraction.NamedEntry> Materials,
    GlbExtraction.Entry? Prefab)
{
    /// <summary>The meta field a generated prefab carries: the guid of the GLB it was generated from.</summary>
    public const string GeneratedFrom = "GeneratedFrom";

    public static readonly Guid MaterialsComponentId = Guid.Parse("bdc4fc87-d7b4-41f1-bc90-fc827005adfc");

    public const string MaterialsComponentType = "Paradise.Export.Data.MaterialsComponentData";

    public static GlbExtraction None { get; } = new(null, null, null, [], [], null);

    public bool Extracted => Mesh is not null;

    /// <param name="GlbFingerprint">SHA-256 of what the GLB extracted to at the last sync.</param>
    /// <param name="DocumentFingerprint">SHA-256 of the document's bytes at the last sync.</param>
    public sealed record Entry(AssetReference Reference, string GlbFingerprint, string DocumentFingerprint);

    public sealed record NamedEntry(string Name, Entry Entry);
}

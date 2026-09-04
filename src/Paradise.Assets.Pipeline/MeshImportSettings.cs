using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>One external file a mesh container names, resolved to an identity.</summary>
/// <param name="Slot">Where in the container the reference sits (<c>images[0]</c>); the key an entry is matched on.</param>
/// <param name="Uri">The uri as the container spells it, relative to itself; recorded so a re-export that changed it can be told from a texture that moved.</param>
/// <param name="Reference">The identity it resolved to, and the assets-relative path that identity lived at.</param>
public readonly record struct MeshReference(string Slot, string Uri, AssetReference Reference);

/// <summary>
/// The <c>[mesh]</c> domain: a mesh container's external references, resolved to identities and
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
public sealed class MeshImportSettings : IImportSettingsDomain
{
    public const string Domain = "mesh";

    public const string ReferencesKey = "references";

    public const string SlotKey = "slot";

    public const string UriKey = "uri";

    public static MeshImportSettings Instance { get; } = new();

    private MeshImportSettings()
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
            if (key != ReferencesKey) return $"holds '{key}' in [{Domain}], which is not a mesh setting";
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

    /// <summary>Records <paramref name="references"/>; an empty list removes the domain, so a mesh with no external files carries no table.</summary>
    public static void Write(SidecarMeta meta, IReadOnlyList<MeshReference> references)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
        {
            meta.RemoveSetting(Domain);
            return;
        }

        var entries = new List<object>();
        foreach (var reference in references)
        {
            var table = new CanonicalInlineTable
            {
                { SlotKey, reference.Slot },
                { UriKey, reference.Uri },
                { AssetReferenceCodec.GuidKey, DocumentGuid.Format(reference.Reference.Guid) },
                { AssetReferenceCodec.PathKey, reference.Reference.Path },
            };
            entries.Add(table);
        }

        meta.SetSetting(Domain, new CanonicalTomlTable { { ReferencesKey, entries } });
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

using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The one walk over every <see cref="AssetReference"/> a document holds, rewriting the ones a
/// policy changes.
/// </summary>
/// <remarks>
/// Two callers with the same walk and different policies: <c>mv</c> follows a file it just moved,
/// and <c>verify --fix</c> catches a stale path up to where the guid says the asset now lives.
/// They were one copied walk before, and a shape one of them learned to visit was a shape the
/// other kept missing.
/// </remarks>
public static class DocumentReferences
{
    /// <summary>Applies <paramref name="follow"/> to every reference; null when nothing changed, so a caller can skip the write.</summary>
    /// <remarks>
    /// A malformed reference — the shape without a usable guid or path — is left exactly as it is:
    /// this walk cannot phrase the finding with the field name that <c>verify</c> gives it, and
    /// rewriting what it could not read would destroy the evidence.
    /// </remarks>
    public static PrefabDocument? Rewrite(PrefabDocument document, Func<AssetReference, AssetReference> follow)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(follow);

        var changed = false;
        var updated = new PrefabDocument();
        foreach (var entry in document.Objects)
        {
            var copy = new PrefabObject { Prefab = entry.Prefab is { } prefab ? Follow(prefab, follow, ref changed) : null };
            foreach (var component in entry.Components)
            {
                copy.Components.Add(new PrefabComponent(
                    component.Id, component.Type, FollowTable(component.Data, follow, ref changed), component.Removed));
            }

            updated.Objects.Add(copy);
        }

        return changed ? updated : null;
    }

    /// <summary>Every well-formed reference the document holds, each with the field path to report it at (<c>game.Mesh.Slots[0]</c>).</summary>
    /// <remarks>The prefab an instance names is yielded as <c>prefab</c>, matching what <c>verify</c> calls it.</remarks>
    public static IEnumerable<(AssetReference Reference, string Where)> Enumerate(PrefabDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var entry in document.Objects)
        {
            if (entry.Prefab is { } prefab) yield return (prefab, "prefab");

            foreach (var component in entry.Components)
            {
                var name = component.Type ?? DocumentGuid.Format(component.Id);
                foreach (var (key, value) in component.Data)
                {
                    foreach (var found in Walk(value, $"{name}.{key}")) yield return found;
                }
            }
        }
    }

    private static IEnumerable<(AssetReference, string)> Walk(object? value, string where)
    {
        switch (value)
        {
            // Gated on the reference SHAPE, not the model type: inside an array every table is
            // inline (#187), so a payload record would otherwise be read as a reference.
            case CanonicalInlineTable table when IsReferenceShaped(table):
                if (AssetReferenceCodec.TryRead(table, out var reference)) yield return (reference, where);
                break;

            case CanonicalTomlTable nested:
                foreach (var (key, member) in nested)
                {
                    foreach (var found in Walk(member, $"{where}.{key}")) yield return found;
                }

                break;

            case IReadOnlyList<object> list:
                for (var i = 0; i < list.Count; i++)
                {
                    foreach (var found in Walk(list[i], $"{where}[{i}]")) yield return found;
                }

                break;
        }
    }

    private static bool IsReferenceShaped(CanonicalInlineTable table)
    {
        var pairs = table.ToList();
        return pairs.Count > 0 && AssetReferenceCodec.IsWrittenInline(pairs);
    }

    private static AssetReference Follow(AssetReference reference, Func<AssetReference, AssetReference> follow, ref bool changed)
    {
        var followed = follow(reference);
        if (followed.Guid == reference.Guid && string.Equals(followed.Path, reference.Path, StringComparison.Ordinal)) return reference;

        changed = true;
        return followed;
    }

    private static CanonicalTomlTable FollowTable(CanonicalTomlTable table, Func<AssetReference, AssetReference> follow, ref bool changed)
    {
        var copy = new CanonicalTomlTable();
        foreach (var (key, value) in table) copy.Add(key, FollowValue(value, follow, ref changed));
        return copy;
    }

    // Shapes mirror TomlDocumentReader.ToCanonicalValue: the inline reference, generic tables,
    // arrays of tables, and plain arrays holding any of those.
    private static object FollowValue(object value, Func<AssetReference, AssetReference> follow, ref bool changed)
    {
        switch (value)
        {
            case CanonicalInlineTable inline:
                return FollowInline(inline, follow, ref changed);

            case CanonicalTomlTable nested:
                return FollowTable(nested, follow, ref changed);

            case IReadOnlyList<CanonicalTomlTable> tables:
            {
                var copies = new CanonicalTomlTable[tables.Count];
                for (var i = 0; i < tables.Count; i++) copies[i] = FollowTable(tables[i], follow, ref changed);
                return copies;
            }

            case IReadOnlyList<object> list:
            {
                var copies = new List<object>(list.Count);
                foreach (var element in list) copies.Add(FollowValue(element, follow, ref changed));
                return copies;
            }

            default:
                return value;
        }
    }

    private static CanonicalInlineTable FollowInline(CanonicalInlineTable inline, Func<AssetReference, AssetReference> follow, ref bool changed)
    {
        if (IsReferenceShaped(inline) && AssetReferenceCodec.TryRead(inline, out var reference))
        {
            var followed = Follow(reference, follow, ref changed);
            // Rewritten through the codec, not key by key: a fix must produce the canonical bytes
            // prefab-check demands, and a hand-edited table can be in any key order.
            return ReferenceEquals(followed, reference) ? inline : AssetReferenceCodec.Write(followed);
        }

        var same = new CanonicalInlineTable();
        foreach (var (key, member) in inline) same.Add(key, FollowValue(member, follow, ref changed));
        return same;
    }
}

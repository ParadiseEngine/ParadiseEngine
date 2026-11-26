using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>Expands prefab instances into plain objects.</summary>
/// <remarks>
/// An instance overrides the prefab root's components by ID; order and generated identities must match the Python mirror.
/// A prefab payload value naming one of the prefab's own members by its local guid is remapped to
/// that member's resolved guid, so an intra-prefab reference names the instance's own member
/// (see <see cref="RemapReferences"/>).
/// </remarks>
public static class PrefabResolver
{
    /// <summary>A problem that stopped one instance resolving, phrased for the author.</summary>
    public readonly record struct ResolveError(string Message)
    {
        /// <inheritdoc />
        public override string ToString() => Message;
    }

    /// <summary>The result of expanding a document.</summary>
    public readonly record struct ResolveResult(PrefabDocument Document, IReadOnlyList<ResolveError> Errors, int Expanded);

    /// <summary>Expands every instance in <paramref name="document"/>.</summary>
    public static ResolveResult Resolve(PrefabDocument document, Func<Paradise.Authoring.AssetReference, PrefabDocument?> prefabs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(prefabs);

        var errors = new List<ResolveError>();
        var resolved = new PrefabDocument();
        var expanded = ExpandDocument(document, prefabs, resolved, errors, [], [], depth: 0);

        return new ResolveResult(resolved, errors, expanded);
    }

    private const int MaxNestingDepth = 32;

    private static int ExpandDocument(
        PrefabDocument document,
        Func<Paradise.Authoring.AssetReference, PrefabDocument?> prefabs,
        PrefabDocument into,
        List<ResolveError> errors,
        List<(Guid Guid, string Path)> stack,
        Dictionary<Guid, PrefabDocument?> cache,
        int depth)
    {
        var expanded = 0;

        var carriers = new Dictionary<(Guid Instance, Guid Target), PrefabObject>();
        foreach (var candidate in document.Objects)
        {
            if (candidate.Target is not { } target) continue;
            if (candidate.Parent is not { } owner)
            {
                errors.Add(new ResolveError(
                    $"an override carrier targeting '{DocumentGuid.Format(target)}' has no Parent naming the instance it belongs to"));
                continue;
            }

            if (!carriers.TryAdd((owner, target), candidate))
            {
                errors.Add(new ResolveError(
                    $"two override carriers address '{DocumentGuid.Format(target)}' on the same instance"));
            }
        }

        foreach (var candidate in document.Objects)
        {
            if (candidate.Target is not null) continue;

            if (candidate.Prefab is null)
            {
                into.Objects.Add(candidate);
                continue;
            }

            // Flatten before expanding so Expand only ever sees a prefab with no instances left
            // in it: nesting then needs no special code.
            var prefab = Flatten(candidate.Prefab, prefabs, errors, stack, cache, depth);
            if (prefab is null) continue;

            if (candidate.Guid is not { } instanceGuid)
            {
                errors.Add(new ResolveError($"an instance of '{candidate.Prefab.Path}' has no meta Guid"));
                continue;
            }

            Expand(candidate, instanceGuid, prefab, carriers, into, errors);
            expanded++;
        }

        return expanded;
    }

    private static PrefabDocument? Flatten(
        Paradise.Authoring.AssetReference reference,
        Func<Paradise.Authoring.AssetReference, PrefabDocument?> prefabs,
        List<ResolveError> errors,
        List<(Guid Guid, string Path)> stack,
        Dictionary<Guid, PrefabDocument?> cache,
        int depth)
    {
        // Keyed on IDENTITY, not on the path: the path half of a reference is a hint that a
        // rename can leave stale, and two spellings of one prefab would then be two cache
        // entries — and a cycle through them would not be seen as one. The path is carried
        // alongside only so the messages below can name something a person recognises.
        var key = reference.Guid;
        var display = reference.Path;

        var loop = stack.FindIndex(entry => entry.Guid == key);
        if (loop >= 0)
        {
            errors.Add(new ResolveError(
                $"prefabs form a cycle: {string.Join(" -> ", stack.Skip(loop).Select(entry => entry.Path).Append(display))}"));
            return null;
        }

        if (depth >= MaxNestingDepth)
        {
            errors.Add(new ResolveError(
                $"prefab '{display}' nests more than {MaxNestingDepth} deep; this is almost certainly a mistake"));
            return null;
        }

        if (cache.TryGetValue(key, out var cached)) return cached;

        var raw = prefabs(reference);
        if (raw is null)
        {
            errors.Add(new ResolveError($"prefab '{display}' could not be read"));
            cache[key] = null;
            return null;
        }

        stack.Add((key, display));
        var flat = new PrefabDocument();
        ExpandDocument(raw, prefabs, flat, errors, stack, cache, depth + 1);
        stack.RemoveAt(stack.Count - 1);

        cache[key] = flat;
        return flat;
    }

    private static void Expand(
        PrefabObject instance,
        Guid instanceGuid,
        PrefabDocument prefab,
        IReadOnlyDictionary<(Guid, Guid), PrefabObject> carriers,
        PrefabDocument into,
        List<ResolveError> errors)
    {
        // Both walk every object; asked per member they made an instance of a large prefab
        // quadratic. The root is still asked for on first use, where it throws for a prefab
        // without a single root.
        Guid? root = null;
        Guid Root() => root ??= prefab.RootGuid;
        var byGuid = prefab.ByGuid();
        var minted = new Dictionary<Guid, Guid>();
        foreach (var member in prefab.Objects)
        {
            if (member.Guid is not { } local) continue;
            minted[local] = local == Root() ? instanceGuid : MintChildGuid(instanceGuid, local);
        }

        // Instance first, then its children in PREFAB order: the runtime assigns entity handles
        // in walk order, so this order is part of the contract.
        foreach (var member in prefab.Objects)
        {
            if (member.Guid is not { } local) continue;

            var isRoot = local == Root();
            var overrides = isRoot ? instance : carriers.GetValueOrDefault((instanceGuid, local));

            if (overrides is { Dropped: true })
            {
                continue;
            }

            if (!isRoot && DropsAncestor(member, byGuid, instanceGuid, carriers)) continue;

            into.Objects.Add(Merge(member, overrides, isRoot, instanceGuid, minted, prefab, errors));
        }

        foreach (var ((owner, target), _) in carriers)
        {
            if (owner != instanceGuid || minted.ContainsKey(target)) continue;
            errors.Add(new ResolveError(
                $"an override on instance '{DocumentGuid.Format(instanceGuid)}' targets " +
                $"'{DocumentGuid.Format(target)}', which '{prefab.Root.Name}' does not contain"));
        }
    }

    private static bool DropsAncestor(
        PrefabObject member,
        Dictionary<Guid, PrefabObject> byGuid,
        Guid instanceGuid,
        IReadOnlyDictionary<(Guid, Guid), PrefabObject> carriers)
    {
        var parent = member.Parent;
        while (parent is { } current)
        {
            if (carriers.GetValueOrDefault((instanceGuid, current)) is { Dropped: true }) return true;
            parent = byGuid.GetValueOrDefault(current)?.Parent;
        }

        return false;
    }

    private static PrefabObject Merge(
        PrefabObject prefabObject,
        PrefabObject? overrides,
        bool isRoot,
        Guid instanceGuid,
        IReadOnlyDictionary<Guid, Guid> minted,
        PrefabDocument prefab,
        List<ResolveError> errors)
    {
        var result = new PrefabObject();

        // Prefab order first, then instance-only additions: part of the byte contract.
        foreach (var prefabComponent in prefabObject.Components)
        {
            var overriding = overrides?.Component(prefabComponent.Id);
            if (overriding is { Removed: true }) continue;

            // The PREFAB's values are remapped before the merge, the instance's never: an override
            // is spelled in the document that holds the instance, where the resolved guids live.
            var component = RemapReferences(prefabComponent, minted);
            result.Components.Add(overriding is null
                ? component
                : new PrefabComponent(component.Id, component.Type ?? overriding.Type, MergeData(component.Data, overriding.Data), removed: false));
        }

        if (overrides is not null)
        {
            foreach (var component in overrides.Components)
            {
                if (prefabObject.Component(component.Id) is not null) continue;
                if (component.Removed)
                {
                    errors.Add(new ResolveError(
                        $"an override removes component '{DocumentGuid.Format(component.Id)}', which " +
                        $"'{prefab.Root.Name}' does not have"));
                    continue;
                }

                result.Components.Add(component);
            }
        }

        RewriteMeta(result, prefabObject, isRoot, instanceGuid, minted, overrides);
        return result;
    }

    private static void RewriteMeta(
        PrefabObject result,
        PrefabObject prefabObject,
        bool isRoot,
        Guid instanceGuid,
        IReadOnlyDictionary<Guid, Guid> minted,
        PrefabObject? overrides)
    {
        var index = result.Components.FindIndex(c => c.Id == WellKnownComponents.MetaId);
        var existing = index >= 0 ? result.Components[index] : null;

        var data = new CanonicalTomlTable();
        var guid = isRoot ? instanceGuid : minted[prefabObject.Guid!.Value];
        data.Add(WellKnownComponents.Guid, DocumentGuid.Format(guid));

        var name = (existing?.Data.Value(WellKnownComponents.Name) as string)
            ?? prefabObject.Name;
        if (name is not null) data.Add(WellKnownComponents.Name, name);

        var parent = isRoot
            ? overrides?.Parent
            : prefabObject.Parent is { } local ? minted.GetValueOrDefault(local) : null;
        if (parent is { } value && value != Guid.Empty)
        {
            data.Add(WellKnownComponents.Parent, DocumentGuid.Format(value));
        }

        // meta is an open payload; a game's extra fields ride along.
        if (existing is not null)
        {
            foreach (var (key, member) in existing.Data)
            {
                if (WellKnownComponents.IsMetaField(key)) continue;

                data.Add(key, member);
            }
        }

        var component = new PrefabComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType, data);
        if (index >= 0) result.Components[index] = component;
        else result.Components.Insert(0, component);
    }

    /// <summary>
    /// <paramref name="component"/> with every payload string naming a member of the prefab by its
    /// prefab-LOCAL guid rewritten to that member's resolved guid, or the component itself when
    /// none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prefab that names one of its own members (an entity reference, a host id) can only spell
    /// the member's local guid, which no object answers to once the prefab is placed: the member
    /// becomes <c>uuid5(instance, local)</c>, the root the instance guid. Remapping here makes such
    /// a reference name THIS instance's member, per instance, and composes through nesting:
    /// flattening rewrites an inner prefab's references to the flattened locals, which the outer
    /// expansion rewrites again.
    /// </para>
    /// <para>
    /// The resolver has no schema, so the rule is by value: a string that parses as a document guid
    /// (<see cref="DocumentGuid.TryParse"/>) and is the local guid of a member of the flattened
    /// prefab, anywhere in a payload (nested tables and arrays included), is written as the member's
    /// resolved guid in canonical form. Everything else is untouched, its spelling included.
    /// <c>meta</c> is skipped (<see cref="RewriteMeta"/> owns identity and parenting), and so is a
    /// table shaped like an asset reference, which names an asset, never an object. Normative: the
    /// Python mirror applies the same rule.
    /// </para>
    /// </remarks>
    private static PrefabComponent RemapReferences(PrefabComponent component, IReadOnlyDictionary<Guid, Guid> minted)
    {
        if (component.Id == WellKnownComponents.MetaId || component.Data.Count == 0) return component;

        var data = RemapTable(component.Data, minted);
        return ReferenceEquals(data, component.Data)
            ? component
            : new PrefabComponent(component.Id, component.Type, data, component.Removed);
    }

    /// <summary>The remapped value, or <paramref name="value"/> itself when nothing in it changed.</summary>
    private static object RemapValue(object value, IReadOnlyDictionary<Guid, Guid> minted) => value switch
    {
        string text when DocumentGuid.TryParse(text, out var local) && minted.TryGetValue(local, out var resolved)
            => DocumentGuid.Format(resolved),
        CanonicalTomlTable table => RemapTable(table, minted),
        CanonicalInlineTable inline => RemapInline(inline, minted),
        // Before the general list: a list of tables is also an IReadOnlyList<object>.
        IReadOnlyList<CanonicalTomlTable> tables => RemapTables(tables, minted),
        IReadOnlyList<object> array => RemapArray(array, minted),
        _ => value,
    };

    private static CanonicalTomlTable RemapTable(CanonicalTomlTable table, IReadOnlyDictionary<Guid, Guid> minted)
    {
        var pairs = table.ToList();
        if (AssetReferenceCodec.IsWrittenInline(pairs)) return table;

        var values = pairs.Select(pair => RemapValue(pair.Value, minted)).ToList();
        if (Unchanged(pairs.Select(pair => pair.Value), values)) return table;

        var remapped = new CanonicalTomlTable();
        for (var index = 0; index < pairs.Count; index++) remapped.Add(pairs[index].Key, values[index]);
        return remapped;
    }

    private static CanonicalInlineTable RemapInline(CanonicalInlineTable table, IReadOnlyDictionary<Guid, Guid> minted)
    {
        if (AssetReferenceCodec.IsWrittenInline(table)) return table;

        var pairs = table.ToList();
        var values = pairs.Select(pair => RemapValue(pair.Value, minted)).ToList();
        if (Unchanged(pairs.Select(pair => pair.Value), values)) return table;

        var remapped = new CanonicalInlineTable();
        for (var index = 0; index < pairs.Count; index++) remapped.Add(pairs[index].Key, values[index]);
        return remapped;
    }

    private static IReadOnlyList<CanonicalTomlTable> RemapTables(
        IReadOnlyList<CanonicalTomlTable> tables, IReadOnlyDictionary<Guid, Guid> minted)
    {
        var remapped = tables.Select(table => RemapTable(table, minted)).ToList();
        return Unchanged(tables, remapped) ? tables : remapped;
    }

    private static IReadOnlyList<object> RemapArray(IReadOnlyList<object> array, IReadOnlyDictionary<Guid, Guid> minted)
    {
        var remapped = array.Select(element => RemapValue(element, minted)).ToList();
        return Unchanged(array, remapped) ? array : remapped;
    }

    private static bool Unchanged<T>(IEnumerable<T> before, List<T> after) where T : class
        => before.Select((value, index) => ReferenceEquals(value, after[index])).All(same => same);

    private static CanonicalTomlTable MergeData(CanonicalTomlTable prefab, CanonicalTomlTable overrides)
    {
        var merged = new CanonicalTomlTable();
        foreach (var (key, value) in prefab)
        {
            merged.Add(key, overrides.Value(key) ?? value);
        }

        // Undeclared fields are added, not refused: refusing would forbid an instance setting
        // meta.Parent (a prefab root has none), the commonest edit there is. Schema is verify's job.
        foreach (var (key, value) in overrides)
        {
            if (!prefab.ContainsKey(key)) merged.Add(key, value);
        }

        return merged;
    }

    /// <summary>A resolved child's scene identity: <c>uuid5(instance guid, prefab-local guid as text)</c>.</summary>
    /// <remarks>
    /// The name is the guid's canonical TEXT, not its bytes: .NET's <see cref="Guid.ToByteArray()"/>
    /// is mixed-endian and Python's <c>UUID.bytes</c> is big-endian, so raw bytes would mint
    /// different identities in the two implementations.
    /// </remarks>
    public static Guid MintChildGuid(Guid instance, Guid prefabLocal)
    {
        var name = Encoding.UTF8.GetBytes(DocumentGuid.Format(prefabLocal));
        var payload = new byte[16 + name.Length];
        instance.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
        name.CopyTo(payload, 16);

        var hash = SHA1.HashData(payload);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);   // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

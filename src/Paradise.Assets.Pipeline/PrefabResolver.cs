using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Expands prefab instances into plain objects.
/// </summary>
/// <remarks>
/// Prefabs are an authoring concept only: the contract carried prefab provenance once and it was
/// deleted in schema v5 as "written by a host, read by nobody". The instance IS the prefab's root
/// (its components override the root's by id). Every order and identity rule below is specified
/// rather than incidental, because the Python mirror must produce the same bytes.
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
        var minted = new Dictionary<Guid, Guid>();
        foreach (var member in prefab.Objects)
        {
            if (member.Guid is not { } local) continue;
            minted[local] = local == prefab.RootGuid ? instanceGuid : MintChildGuid(instanceGuid, local);
        }

        // Instance first, then its children in PREFAB order: the runtime assigns entity handles
        // in walk order, so this order is part of the contract.
        foreach (var member in prefab.Objects)
        {
            if (member.Guid is not { } local) continue;

            var isRoot = local == prefab.RootGuid;
            var overrides = isRoot ? instance : carriers.GetValueOrDefault((instanceGuid, local));

            if (overrides is { Dropped: true })
            {
                continue;
            }

            if (!isRoot && DropsAncestor(member, prefab, instanceGuid, carriers)) continue;

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
        PrefabDocument prefab,
        Guid instanceGuid,
        IReadOnlyDictionary<(Guid, Guid), PrefabObject> carriers)
    {
        var byGuid = prefab.ByGuid();
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
        foreach (var component in prefabObject.Components)
        {
            var overriding = overrides?.Component(component.Id);
            if (overriding is { Removed: true }) continue;

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

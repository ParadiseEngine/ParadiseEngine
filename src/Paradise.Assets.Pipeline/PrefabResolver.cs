using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Documents;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Expands prefab instances into plain objects.
/// </summary>
/// <remarks>
/// <para>
/// Prefabs are an AUTHORING concept. Nothing downstream knows about them: the build flattens an
/// instance into ordinary objects and the export contract, the loader and the runtime see exactly
/// what a hand-placed object would have produced. That is deliberate — the contract carried
/// prefab provenance once and it was deleted in schema v5 as "written by a host, read by nobody".
/// </para>
/// <para>
/// <b>The instance IS the prefab's root.</b> Its components override the root's, matched by
/// component id; the prefab's other objects resolve beneath it. Everything order- or
/// identity-related below is specified rather than left to the implementation, because the Python
/// mirror has to produce the same documents byte for byte.
/// </para>
/// </remarks>
public static class PrefabResolver
{
    /// <summary>A problem that stopped one instance resolving.</summary>
    /// <param name="Message">What is wrong, phrased for the author.</param>
    public readonly record struct ResolveError(string Message)
    {
        /// <inheritdoc />
        public override string ToString() => Message;
    }

    /// <summary>The result of expanding a document.</summary>
    /// <param name="Document">The flattened document; every object is plain.</param>
    /// <param name="Errors">What could not be resolved; empty on success.</param>
    /// <param name="Expanded">How many instances were expanded.</param>
    public readonly record struct ResolveResult(SceneDocument Document, IReadOnlyList<ResolveError> Errors, int Expanded);

    /// <summary>
    /// Expands every instance in <paramref name="document"/>.
    /// </summary>
    /// <param name="document">The scene, which may contain instances and override carriers.</param>
    /// <param name="prefabs">Resolves a prefab reference to its document, or returns null.</param>
    public static ResolveResult Resolve(SceneDocument document, Func<Paradise.Authoring.AssetReference, PrefabDocument?> prefabs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(prefabs);

        var errors = new List<ResolveError>();
        var resolved = new SceneDocument();
        var expanded = 0;

        // Carriers are consumed by the instance they belong to and occupy no slot of their own,
        // so they are collected first and looked up by (instance guid, prefab-local target).
        var carriers = new Dictionary<(Guid Instance, Guid Target), SceneObject>();
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
            if (candidate.Target is not null) continue;   // consumed above

            if (candidate.Prefab is null)
            {
                resolved.Objects.Add(candidate);
                continue;
            }

            var prefab = prefabs(candidate.Prefab);
            if (prefab is null)
            {
                errors.Add(new ResolveError($"prefab '{candidate.Prefab.Path}' could not be read"));
                continue;
            }

            if (candidate.Guid is not { } instanceGuid)
            {
                errors.Add(new ResolveError($"an instance of '{candidate.Prefab.Path}' has no meta Guid"));
                continue;
            }

            Expand(candidate, instanceGuid, prefab, carriers, resolved, errors);
            expanded++;
        }

        return new ResolveResult(resolved, errors, expanded);
    }

    private static void Expand(
        SceneObject instance,
        Guid instanceGuid,
        PrefabDocument prefab,
        IReadOnlyDictionary<(Guid, Guid), SceneObject> carriers,
        SceneDocument into,
        List<ResolveError> errors)
    {
        // Every prefab-local identity gets its scene identity up front, so a Parent link can be
        // remapped whichever order the objects come in.
        var minted = new Dictionary<Guid, Guid>();
        foreach (var member in prefab.Document.Objects)
        {
            if (member.Guid is not { } local) continue;
            minted[local] = local == prefab.RootGuid ? instanceGuid : MintChildGuid(instanceGuid, local);
        }

        // ORDER: the instance first, then its children in PREFAB document order, immediately
        // after it. Order is load-bearing -- the runtime assigns entity handles in walk order --
        // so it is specified here rather than left to whatever the loop happens to do.
        foreach (var member in prefab.Document.Objects)
        {
            if (member.Guid is not { } local) continue;

            var isRoot = local == prefab.RootGuid;
            var overrides = isRoot ? instance : carriers.GetValueOrDefault((instanceGuid, local));

            if (overrides is { Dropped: true })
            {
                continue;   // and its descendants, handled below
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

    /// <summary>Whether any ancestor of <paramref name="member"/> is dropped by this instance.</summary>
    private static bool DropsAncestor(
        SceneObject member,
        PrefabDocument prefab,
        Guid instanceGuid,
        IReadOnlyDictionary<(Guid, Guid), SceneObject> carriers)
    {
        var byGuid = prefab.Document.ByGuid();
        var parent = member.Parent;
        while (parent is { } current)
        {
            if (carriers.GetValueOrDefault((instanceGuid, current)) is { Dropped: true }) return true;
            parent = byGuid.GetValueOrDefault(current)?.Parent;
        }

        return false;
    }

    private static SceneObject Merge(
        SceneObject prefabObject,
        SceneObject? overrides,
        bool isRoot,
        Guid instanceGuid,
        IReadOnlyDictionary<Guid, Guid> minted,
        PrefabDocument prefab,
        List<ResolveError> errors)
    {
        var result = new SceneObject();

        // COMPONENT ORDER: prefab order first, then instance-only additions in instance order.
        foreach (var component in prefabObject.Components)
        {
            var overriding = overrides?.Component(component.Id);
            if (overriding is { Removed: true }) continue;

            result.Components.Add(overriding is null
                ? component
                : new SceneComponent(component.Id, component.Type ?? overriding.Type, MergeData(component.Data, overriding.Data), removed: false));
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

    /// <summary>
    /// Rewrites the resolved object's meta: minted identity, remapped parent, and none of the
    /// carrier-only fields, which describe the override rather than the object.
    /// </summary>
    private static void RewriteMeta(
        SceneObject result,
        SceneObject prefabObject,
        bool isRoot,
        Guid instanceGuid,
        IReadOnlyDictionary<Guid, Guid> minted,
        SceneObject? overrides)
    {
        var index = result.Components.FindIndex(c => c.Id == WellKnownComponents.MetaId);
        var existing = index >= 0 ? result.Components[index] : null;

        var data = new CanonicalTomlTable();
        var guid = isRoot ? instanceGuid : minted[prefabObject.Guid!.Value];
        data.Add(WellKnownComponents.Guid, DocumentGuid.Format(guid));

        var name = (existing?.Data.Value(WellKnownComponents.Name) as string)
            ?? prefabObject.Name;
        if (name is not null) data.Add(WellKnownComponents.Name, name);

        // The ROOT's parent comes from the instance -- that is how a prefab is placed under
        // something. A child's comes from the prefab, remapped to the minted identity.
        var parent = isRoot
            ? overrides?.Parent
            : prefabObject.Parent is { } local ? minted.GetValueOrDefault(local) : null;
        if (parent is { } value && value != Guid.Empty)
        {
            data.Add(WellKnownComponents.Parent, DocumentGuid.Format(value));
        }

        // Anything else the prefab's meta carried (a game does not extend meta today, but the
        // payload is open) rides along, minus the carrier-only fields.
        if (existing is not null)
        {
            foreach (var (key, member) in existing.Data)
            {
                if (key is WellKnownComponents.Guid or WellKnownComponents.Name or WellKnownComponents.Parent
                    or WellKnownComponents.Target or WellKnownComponents.Dropped)
                {
                    continue;
                }

                data.Add(key, member);
            }
        }

        var component = new SceneComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType, data);
        if (index >= 0) result.Components[index] = component;
        else result.Components.Insert(0, component);
    }

    /// <summary>Field-by-field override: the instance's value wins, everything else is inherited.</summary>
    private static CanonicalTomlTable MergeData(CanonicalTomlTable prefab, CanonicalTomlTable overrides)
    {
        var merged = new CanonicalTomlTable();
        foreach (var (key, value) in prefab)
        {
            merged.Add(key, overrides.Value(key) ?? value);
        }

        // Fields the prefab's component does not declare are ADDED, not refused. Whether a field
        // belongs to the component at all is a SCHEMA question, and verify answers it there --
        // refusing here would forbid an instance setting meta.Parent, since a prefab root
        // deliberately has none, and that is the commonest edit there is.
        foreach (var (key, value) in overrides)
        {
            if (!prefab.ContainsKey(key)) merged.Add(key, value);
        }

        return merged;
    }

    /// <summary>
    /// A resolved child's scene identity: <c>uuid5(instance guid, prefab-local guid as text)</c>.
    /// </summary>
    /// <remarks>
    /// Deterministic, so the same scene resolves to the same identities on every machine and the
    /// export is reproducible. The namespace is the INSTANCE, so twenty instances of one prefab
    /// give twenty distinct sets of children with no bookkeeping in the document.
    /// <para>
    /// The name is the guid's canonical TEXT, not its bytes. .NET's <see cref="Guid.ToByteArray()"/>
    /// is mixed-endian and Python's <c>UUID.bytes</c> is big-endian, so hashing raw bytes would
    /// mint DIFFERENT identities in the two implementations — a divergence that would only show
    /// up as a mismatched document long after the fact.
    /// </para>
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

using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// The authoring document — the committed source of truth a <c>*.prefab</c> holds, whether a game
/// calls it a level, a prop, or a piece of one.
/// </summary>
/// <remarks>
/// Not the export contract: that is a bake, and this keeps exactly what baking destroys. Identity,
/// name, parent and placement are components (<see cref="WellKnownComponents"/>) so that one
/// override mechanism covers everything.
/// </remarks>
public sealed class PrefabDocument
{
    /// <summary>The only <c>schema_version</c> this build reads or writes.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The document's objects, in document order. Order is load-bearing.</summary>
    public List<PrefabObject> Objects { get; } = [];

    /// <summary>
    /// Objects by their <c>meta.Guid</c>. Last wins on a duplicate — this is a lookup, not a
    /// validator; callers wanting duplicates refused go through <c>Validate</c> first (issue #210).
    /// </summary>
    public Dictionary<Guid, PrefabObject> ByGuid()
    {
        var map = new Dictionary<Guid, PrefabObject>();
        foreach (var candidate in Objects)
        {
            if (candidate.Guid is { } guid) map[guid] = candidate;
        }

        return map;
    }

    /// <summary>The single parentless object, or <see langword="null"/>; inferred so nothing can disagree with the hierarchy.</summary>
    public PrefabObject? SingleRoot()
    {
        PrefabObject? root = null;
        foreach (var candidate in Objects)
        {
            if (candidate.Parent is not null) continue;
            if (root is not null) return null;
            root = candidate;
        }

        return root;
    }

    /// <summary>The root, for a document already known to be valid.</summary>
    /// <exception cref="InvalidOperationException">The document has no single root.</exception>
    public PrefabObject Root =>
        SingleRoot() ?? throw new InvalidOperationException("this document has no single root");

    /// <summary>The root's identity.</summary>
    public Guid RootGuid => Root.Guid!.Value;

    /// <summary>Requires exactly one root, because an instance places exactly one thing; that is what lets any document be instantiated into any other.</summary>
    /// <exception cref="PrefabDocumentException">No objects, no root, or several roots.</exception>
    public void Validate(string sourceName)
    {
        if (Objects.Count == 0) throw new PrefabDocumentException(sourceName, "has no objects");

        var roots = Objects.Where(o => o.Parent is null).ToList();
        if (roots.Count == 1) return;

        if (roots.Count == 0)
        {
            throw new PrefabDocumentException(sourceName, "has no root object (every object declares a parent)");
        }

        var names = string.Join(", ", roots.Select(r => r.Name ?? DocumentGuid.Format(r.Guid ?? Guid.Empty)));
        throw new PrefabDocumentException(
            sourceName,
            $"has {roots.Count} root objects ({names}); a document has exactly one, because an " +
            "instance places exactly one thing — parent the others beneath it");
    }
}

/// <summary>One authored object: a prefab reference, if any, and its components.</summary>
public sealed class PrefabObject
{
    /// <summary>The prefab this object instantiates, or <see langword="null"/>; an instance IS the prefab's root.</summary>
    public AssetReference? Prefab { get; set; }

    /// <summary>Component entries in document order. Order is data — the bake preserves it.</summary>
    public List<PrefabComponent> Components { get; } = [];

    /// <summary>An object carrying just a <c>meta</c> component; hand-built meta is easy to get subtly wrong (unformatted guid, empty parent instead of none).</summary>
    public static PrefabObject WithMeta(Guid guid, string? name = null, Guid? parent = null)
    {
        var data = new CanonicalTomlTable { { WellKnownComponents.Guid, DocumentGuid.Format(guid) } };
        if (name is not null) data.Add(WellKnownComponents.Name, name);
        if (parent is { } value) data.Add(WellKnownComponents.Parent, DocumentGuid.Format(value));

        var entry = new PrefabObject();
        entry.Components.Add(new PrefabComponent(
            WellKnownComponents.MetaId, WellKnownComponents.MetaType, data));
        return entry;
    }

    /// <summary>This object's <c>meta</c> component, when it has one.</summary>
    public PrefabComponent? Meta => Component(WellKnownComponents.MetaId);

    /// <summary>Identity, from <c>meta.Guid</c>.</summary>
    public Guid? Guid => MetaGuid(WellKnownComponents.Guid);

    /// <summary>Display name, from <c>meta.Name</c>.</summary>
    public string? Name => Meta?.Data.Value(WellKnownComponents.Name) as string;

    /// <summary>The parent's identity, from <c>meta.Parent</c>.</summary>
    public Guid? Parent => MetaGuid(WellKnownComponents.Parent);

    /// <summary>The prefab-local object this overrides, from <c>meta.Target</c>.</summary>
    public Guid? Target => MetaGuid(WellKnownComponents.Target);

    /// <summary>Whether this carrier drops the prefab child it targets.</summary>
    public bool Dropped => Meta?.Data.Value(WellKnownComponents.Dropped) is true;

    /// <summary>The component with <paramref name="id"/>, or <see langword="null"/>.</summary>
    public PrefabComponent? Component(Guid id)
    {
        foreach (var candidate in Components)
        {
            if (candidate.Id == id) return candidate;
        }

        return null;
    }

    private Guid? MetaGuid(string field)
    {
        if (Meta?.Data.Value(field) is not string text) return null;
        return DocumentGuid.TryParse(text, out var guid) && guid != System.Guid.Empty ? guid : null;
    }
}

/// <summary>One component entry, its payload flattened beside <c>id</c> and <c>type</c> rather than nested.</summary>
/// <remarks>
/// Flattening costs three reserved names and buys about a quarter of a document's lines; the
/// constructor refuses a payload using one so the error names the code that built it. (Parsed
/// text relies on the serializer consuming reserved keys first, not on the parser refusing
/// duplicates — Tomlyn's default is last-wins, issue #198.)
/// </remarks>
public sealed class PrefabComponent
{
    /// <summary>The reserved key holding the component's identity.</summary>
    public const string IdKey = "id";

    /// <summary>The reserved key holding the component's readable name.</summary>
    public const string TypeKey = "type";

    /// <summary>The reserved key marking a component the instance drops.</summary>
    public const string RemovedKey = "removed";

    /// <summary>The three keys a payload may not use.</summary>
    public static readonly string[] ReservedKeys = [IdKey, TypeKey, RemovedKey];

    /// <summary>Whether <paramref name="key"/> is one of <see cref="ReservedKeys"/>.</summary>
    public static bool IsReserved(string key) => key is IdKey or TypeKey or RemovedKey;

    /// <summary>Creates a component entry; <paramref name="id"/> is what an override matches on, <paramref name="type"/> is for humans only.</summary>
    /// <exception cref="ArgumentException">The payload uses a reserved key.</exception>
    public PrefabComponent(Guid id, string? type = null, CanonicalTomlTable? data = null, bool removed = false)
    {
        Id = id;
        Type = type;
        Data = data ?? new CanonicalTomlTable();
        Removed = removed;

        foreach (var (key, _) in Data)
        {
            if (IsReserved(key))
            {
                throw new ArgumentException(
                    $"A component payload may not use the reserved key '{key}'; on the wire it would collide with the component's own structure.",
                    nameof(data));
            }
        }
    }

    /// <summary>The component's identity.</summary>
    public Guid Id { get; }

    /// <summary>Its readable name. Optional.</summary>
    public string? Type { get; }

    /// <summary>The authored payload.</summary>
    public CanonicalTomlTable Data { get; }

    /// <summary>On an instance: drop the prefab's component of this id rather than overriding it.</summary>
    public bool Removed { get; }
}

/// <summary>A prefab document could not be read, parsed, or validated.</summary>
public sealed class PrefabDocumentException : Exception
{
    /// <summary>Creates an exception; <paramref name="problem"/> is phrased to follow the source name.</summary>
    public PrefabDocumentException(string sourceName, string problem, Exception? innerException = null)
        : base($"Prefab document '{sourceName}' {problem}.", innerException)
    {
        SourceName = sourceName;
    }

    /// <summary>The document this failure is about.</summary>
    public string SourceName { get; }
}

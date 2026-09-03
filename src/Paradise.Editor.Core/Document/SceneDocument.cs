using System.Collections.Immutable;
using Paradise.Assets.Documents;

namespace Paradise.Editor.Core.Document;

/// <summary>One component on a scene object: the authored id, the declared type name, and the
/// payload exactly as the file holds it.</summary>
/// <remarks><see cref="Data"/> is the authored table, never a deserialized game type: the editor
/// cannot name the type, which is the whole point of the schema-driven inspector. The table is
/// treated as frozen once it is inside a document; an edit produces a new table.</remarks>
public sealed record SceneComponent(Guid Id, string? Type, CanonicalTomlTable Data);

/// <summary>One object in a scene: its identity and its components, in document order.</summary>
/// <remarks>
/// <para>
/// Name and parent are READ OUT OF the <c>meta</c> component rather than stored beside it, because
/// <c>meta</c> is where the file keeps them (<c>PrefabObject.Name</c>, <c>.Parent</c>) and a
/// component list documented as "exactly as the file holds it" cannot also be shadowed by fields
/// that drift from it. A renamed object with a stale <c>meta.Name</c> is not a bug anyone would
/// see until save time, and then only in whichever of the two the writer happened to read.
/// </para>
/// <para>
/// <see cref="Id"/> is the exception and is stored: it is <c>meta.Guid</c>, but it is read on
/// every lookup and never edited, so it is established once by whoever builds the object — use
/// <see cref="WithMeta"/> — and treated as an invariant from there. Everything else <c>meta</c>
/// carries (<c>Target</c>, <c>Dropped</c>, and any field a future format adds) needs no accessor
/// here: it rides along inside <see cref="Components"/> and survives a round trip untouched.
/// </para>
/// </remarks>
public sealed record SceneObject(NodeId Id, ImmutableList<SceneComponent> Components)
{
    /// <summary>An object carrying just a <c>meta</c> component, formatted the way the document
    /// contract spells it.</summary>
    public static SceneObject WithMeta(NodeId id, string? name = null, NodeId? parent = null)
    {
        var data = new CanonicalTomlTable { { WellKnownComponents.Guid, DocumentGuid.Format(id.Value) } };
        if (name is not null) data.Add(WellKnownComponents.Name, name);
        if (parent is { } value) data.Add(WellKnownComponents.Parent, DocumentGuid.Format(value.Value));

        return new SceneObject(id, [new SceneComponent(WellKnownComponents.MetaId, WellKnownComponents.MetaType, data)]);
    }

    public SceneComponent? Meta => Component(WellKnownComponents.MetaId);

    /// <summary>Display name, from <c>meta.Name</c>. Not identity, and not unique.</summary>
    public string? Name => Meta?.Data.Value(WellKnownComponents.Name) as string;

    /// <summary>The parent's identity, from <c>meta.Parent</c>.</summary>
    public NodeId? Parent =>
        Meta?.Data.Value(WellKnownComponents.Parent) is string text && DocumentGuid.TryParse(text, out var guid)
            ? new NodeId(guid)
            : null;

    public SceneComponent? Component(Guid id)
    {
        foreach (var candidate in Components)
        {
            if (candidate.Id == id) return candidate;
        }
        return null;
    }

    public SceneObject WithName(string? name) => WithMetaField(WellKnownComponents.Name, name);

    public SceneObject WithParent(NodeId? parent) =>
        WithMetaField(WellKnownComponents.Parent, parent is { } value ? DocumentGuid.Format(value.Value) : null);

    // Rebuilt rather than mutated because a table inside a document is frozen, and rebuilt IN
    // ORDER because CanonicalTomlTable's key order IS the file's: writing a renamed object would
    // otherwise move Name to the end and show up as a diff nobody made.
    private SceneObject WithMetaField(string key, object? value)
    {
        if (Components.FindIndex(component => component.Id == WellKnownComponents.MetaId) is var index && index < 0)
        {
            throw new InvalidOperationException($"Object '{Id}' has no meta component to write '{key}' into.");
        }

        var meta = Components[index];
        var rebuilt = new CanonicalTomlTable();
        var replaced = false;
        foreach (var (existing, held) in meta.Data)
        {
            if (existing == key)
            {
                replaced = true;
                if (value is not null) rebuilt.Add(key, value);
            }
            else
            {
                rebuilt.Add(existing, held);
            }
        }
        if (!replaced && value is not null) rebuilt.Add(key, value);

        return this with { Components = Components.SetItem(index, meta with { Data = rebuilt }) };
    }
}

/// <summary>The editor's working copy of an authored scene, as an immutable value.</summary>
/// <remarks>Immutability is what makes undo a list of versions instead of a list of inverses: an
/// edit returns a new document that shares every unchanged object with the previous one, and
/// undo republishes an earlier document. Nothing mutates in place, so an observer can diff two
/// versions by reference equality. Object order is preserved because the runtime assigns entity
/// handles in document order.</remarks>
public sealed record SceneDocument(ImmutableList<SceneObject> Objects)
{
    public static SceneDocument Empty { get; } = new(ImmutableList<SceneObject>.Empty);

    public SceneObject? Find(NodeId id)
    {
        foreach (var candidate in Objects)
        {
            if (candidate.Id == id) return candidate;
        }
        return null;
    }

    public IEnumerable<SceneObject> ChildrenOf(NodeId? parent)
    {
        foreach (var candidate in Objects)
        {
            if (candidate.Parent == parent) yield return candidate;
        }
    }

    public SceneDocument Replace(SceneObject updated) =>
        this with { Objects = Objects.Replace(Find(updated.Id) ?? throw new KeyNotFoundException(updated.Id.ToString()), updated) };
}

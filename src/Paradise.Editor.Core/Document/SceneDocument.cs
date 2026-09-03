using System.Collections.Immutable;
using Paradise.Assets.Documents;

namespace Paradise.Editor.Core.Document;

/// <summary>One component on a scene object: the authored id, the declared type name, and the
/// payload exactly as the file holds it.</summary>
/// <remarks><see cref="Data"/> is the authored table, never a deserialized game type: the editor
/// cannot name the type, which is the whole point of the schema-driven inspector. The table is
/// treated as frozen once it is inside a document; an edit produces a new table.</remarks>
public sealed record SceneComponent(Guid Id, string? Type, CanonicalTomlTable Data);

/// <summary>One object in a scene: identity, hierarchy and its components, in document order.</summary>
public sealed record SceneObject(
    NodeId Id,
    string? Name,
    NodeId? Parent,
    ImmutableList<SceneComponent> Components);

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

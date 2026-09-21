using System.Collections.Immutable;
using Paradise.Editor.Core.Document;

namespace Paradise.Editor.Core.Selection;

/// <summary>The selected objects, with the last one selected as primary.</summary>
/// <remarks>Editor state, never document state: it is not in the undo history, and it survives
/// undo because node ids are durable. An object that no longer exists is pruned, not restored.</remarks>
public sealed record Selection(ImmutableList<NodeId> Nodes)
{
    public static Selection Empty { get; } = new(ImmutableList<NodeId>.Empty);

    public NodeId? Primary => Nodes.Count == 0 ? null : Nodes[^1];

    public bool Contains(NodeId id) => Nodes.Contains(id);

    public Selection Only(NodeId id) => new([id]);

    public Selection With(NodeId id) => Contains(id) ? this : new(Nodes.Add(id));

    public Selection Without(NodeId id) => new(Nodes.Remove(id));

    public Selection PrunedTo(SceneDocument document) =>
        new(Nodes.RemoveAll(id => document.Find(id) is null));
}

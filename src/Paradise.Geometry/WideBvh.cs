namespace Paradise.Geometry;

/// <summary>Stores an 8-wide quantized BVH with its root at node zero.</summary>
/// <remarks>Leaves index contiguous runs in ItemOrder, which maps leaf slots to caller item indices.
/// Consumers arrange their records in that order so LeafBase remains a plain offset.</remarks>
public sealed class WideBvh
{
    public WideBvh(BvhNode[] nodes, int[] itemOrder, Aabb bounds)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(itemOrder);
        if (nodes.Length == 0) throw new ArgumentException("A hierarchy has at least its root node.", nameof(nodes));
        Nodes = nodes;
        ItemOrder = itemOrder;
        Bounds = bounds;
        Height = HeightOf(nodes, 0);
    }

    /// <summary>Internal levels from the root to the deepest node (a lone root is 1).</summary>
    public int Height { get; }

    /// <summary>The stack capacity needed for seven pending siblings per level plus eight pushed children.</summary>
    /// <remarks>Consumers must compare this bound with their traversal stack capacity.</remarks>
    public int RequiredStackDepth => 7 * Height + 8;

    private static int HeightOf(BvhNode[] nodes, int index)
    {
        var node = nodes[index];
        var deepest = 0;
        for (var child = 0; child < BvhNode.ChildCount; child++)
        {
            var meta = node.GetMeta(child);
            if (!BvhNode.IsInternal(meta)) continue;
            deepest = Math.Max(deepest, HeightOf(nodes, (int)node.ChildBase + BvhNode.InternalSlot(meta)));
        }
        return deepest + 1;
    }

    public BvhNode[] Nodes { get; }

    /// <summary>Leaf slot → caller's item index.</summary>
    public int[] ItemOrder { get; }

    /// <summary>The exact bounds of every item, or <see cref="Aabb.Empty"/> for no items.</summary>
    public Aabb Bounds { get; }

    public int ItemCount => ItemOrder.Length;
}

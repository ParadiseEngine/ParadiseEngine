namespace Paradise.Geometry;

/// <summary>An 8-wide quantized bounding volume hierarchy over items — triangles of one mesh, or
/// the instances of a scene — in the layout the compute tracer reads. Root at node 0.
///
/// <para>Leaves do not store items; they store runs into <see cref="ItemOrder"/>, which maps each
/// leaf slot back to the caller's item index. A consumer lays its own item records out in this
/// order (the renderer writes its triangle buffer in it), so a leaf's items are contiguous in
/// memory and a node's <see cref="BvhNode.LeafBase"/> is a plain offset.</para></summary>
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

    /// <summary>The stack a depth-first walk needs: popping a node at each level can leave up to
    /// seven siblings pending, plus the eight children it pushes. A walk with a shallower stack
    /// drops children silently, so the consumer checks this against its stack constant.</summary>
    public int RequiredStackDepth => 7 * Height + 8;

    private static int HeightOf(BvhNode[] nodes, int index)
    {
        var node = nodes[index];
        var deepest = 0;
        for (var child = 0; child < BvhNode.ChildCount; child++)
        {
            var meta = node.GetMeta(child);
            if (meta == BvhNode.EmptyChild || !BvhNode.IsInternal(meta)) continue;
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

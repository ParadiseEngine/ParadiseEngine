using Paradise.BLOB;

namespace Paradise.BT.Builder;

public abstract class BTreeNode
{
    internal BTreeNode()
    {
    }

    internal abstract INodeBuilder Builder { get; }

    internal abstract int ChildCount { get; }

    /// <summary>
    /// Compiles this node, as the root, into the shared layout instances tick against. The
    /// caller owns the layout and disposes it once nothing does.
    /// </summary>
    public BehaviorTreeLayout Build()
    {
        var nodes = new List<INodeBuilder>();
        Compile(nodes);
        return BuildLayout(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nodes));
    }

    /// <inheritdoc cref="Build()"/>
    public static BehaviorTreeLayout Build(BTreeNode root)
        => root is null ? throw new ArgumentNullException(nameof(root)) : root.Build();

    internal void Compile(List<INodeBuilder> nodes)
    {
        INodeBuilder builder = Builder;
        ValidateChildCount(builder);

        nodes.Add(builder);
        CompileChildren(nodes);
        builder.EndIndex = nodes.Count;
    }

    internal abstract void CompileChildren(List<INodeBuilder> nodes);

    /// <summary>
    /// The builder's arity checked against the node's <c>[Builder]</c> cardinality — a wrong-arity
    /// wrapper otherwise misbehaves SILENTLY, since traversal never visits the impossible child.
    /// </summary>
    private void ValidateChildCount(INodeBuilder builder)
    {
        switch (builder.Cardinality)
        {
            case NodeCardinality.Leaf when ChildCount != 0:
                throw new InvalidOperationException(
                    $"Leaf node '{builder.NodeType.FullName}' cannot have children, got "
                    + $"{ChildCount}.");
            case NodeCardinality.Decorator when ChildCount != 1:
                throw new InvalidOperationException(
                    $"Decorator node '{builder.NodeType.FullName}' must have exactly one "
                    + $"child, got {ChildCount}.");
        }
    }

    private static BehaviorTreeLayout BuildLayout(ReadOnlySpan<INodeBuilder> compiledNodes, int alignment = 16)
    {
        int count = compiledNodes.Length;
        var typeIds = new int[count];
        var endIndices = new int[count];
        var offsets = new int[count + 1];

        // One GUID per distinct type, ordered by first appearance. Types stores the table INDEX.
        var guidTable = new List<Guid>();
        var tableIndexByGuid = new Dictionary<Guid, int>();

        int size = 0;
        for (int i = 0; i < count; i++)
        {
            INodeBuilder builder = compiledNodes[i];
            if (!NodeTypeRegistry.IsRegistered(builder.NodeGuid))
            {
                throw new InvalidOperationException(
                    $"Node type '{builder.NodeType.FullName}' (GUID '{builder.NodeGuid}') "
                    + "is not registered. Node types register themselves through the module "
                    + "initializer the BT generator emits, so this usually means the declaring "
                    + "project does not reference Paradise.BT.Generators as an analyzer, or the "
                    + $"type is not visible to it. Call {nameof(NodeTypeRegistry)}.Register<T>() "
                    + "explicitly for such a node.");
            }

            if (!tableIndexByGuid.TryGetValue(builder.NodeGuid, out int tableIndex))
            {
                tableIndex = guidTable.Count;
                tableIndexByGuid[builder.NodeGuid] = tableIndex;
                guidTable.Add(builder.NodeGuid);
            }

            typeIds[i] = tableIndex;
            size = Align(size, builder.DataAlignment);
            offsets[i] = size;
            size += builder.DataSize;
        }

        offsets[count] = size;

        // Padding between nodes stays zeroed, which is what a fresh array gives us.
        var defaultData = new byte[size];
        for (int i = 0; i < count; i++)
        {
            INodeBuilder node = compiledNodes[i];
            endIndices[i] = node.EndIndex;
            node.WriteDefaultData(defaultData.AsSpan(offsets[i], node.DataSize));
        }

        var blobBuilder = new StructBuilder<BehaviorTreeLayout.LayoutBlob>();
        blobBuilder.SetArray(ref blobBuilder.Value.EndIndices, endIndices, alignment);
        blobBuilder.SetArray(ref blobBuilder.Value.Types, typeIds, alignment);
        blobBuilder.SetArray(ref blobBuilder.Value.Guids, guidTable, alignment);
        blobBuilder.SetArray(ref blobBuilder.Value.Offsets, offsets, alignment);
        blobBuilder.SetArray(ref blobBuilder.Value.DefaultData, defaultData, alignment);
        return new BehaviorTreeLayout(blobBuilder.CreateNativeBlobAssetReference(alignment));
    }

    private static int Align(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);

}

/// <summary>
/// The data-carrying half of the three arity builders. Metadata (GUID, cardinality) is reflected
/// once per node type.
/// </summary>
public abstract class BTreeNode<TNode> : BTreeNode where TNode : struct, INode
{
    private static readonly BehaviorNodeMetadata s_metadata = new(typeof(TNode));

    private readonly TNode _data;

    private protected BTreeNode(TNode data) => _data = data;

    internal sealed override INodeBuilder Builder
        => new NodeBuilder<TNode>(_data, s_metadata);
}

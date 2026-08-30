namespace Paradise.BT.Builder;

public abstract class BTreeNode
{
    internal BTreeNode()
    {
    }

    internal abstract IRuntimeNodeFactory Factory { get; }

    internal abstract int ChildCount { get; }

    /// <summary>
    /// Compiles this node, as the root, into the shared layout instances tick against. The
    /// caller owns the layout and disposes it once nothing does.
    /// </summary>
    public BehaviorTreeLayout Build()
    {
        var nodes = new List<BehaviorTreeNode>();
        Compile(nodes);
        return BehaviorTreeLayout.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nodes));
    }

    /// <inheritdoc cref="Build()"/>
    public static BehaviorTreeLayout Build(BTreeNode root)
        => root is null ? throw new ArgumentNullException(nameof(root)) : root.Build();

    internal void Compile(List<BehaviorTreeNode> nodes)
    {
        IRuntimeNodeFactory factory = Factory;
        ValidateChildCount(factory);

        int index = nodes.Count;
        nodes.Add(default);
        CompileChildren(nodes);
        nodes[index] = new BehaviorTreeNode(factory, nodes.Count);
    }

    internal abstract void CompileChildren(List<BehaviorTreeNode> nodes);

    /// <summary>
    /// The builder's arity checked against the node's <c>[Builder]</c> cardinality — a wrong-arity
    /// wrapper otherwise misbehaves SILENTLY, since traversal never visits the impossible child.
    /// </summary>
    private void ValidateChildCount(IRuntimeNodeFactory factory)
    {
        switch (factory.Cardinality)
        {
            case NodeCardinality.Leaf when ChildCount != 0:
                throw new InvalidOperationException(
                    $"Leaf node '{factory.NodeType.FullName}' cannot have children, got "
                    + $"{ChildCount}.");
            case NodeCardinality.Decorator when ChildCount != 1:
                throw new InvalidOperationException(
                    $"Decorator node '{factory.NodeType.FullName}' must have exactly one "
                    + $"child, got {ChildCount}.");
        }
    }
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

    internal sealed override IRuntimeNodeFactory Factory
        => new RuntimeNodeFactory<TNode>(_data, s_metadata);
}

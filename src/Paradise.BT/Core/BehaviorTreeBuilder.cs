namespace Paradise.BT;

/// <summary>
/// Compiles authored node graphs into a flat runtime tree.
/// </summary>
public sealed class BehaviorTreeBuilder
{
    private readonly BehaviorNodeDefinition _root;

    public BehaviorTreeBuilder(BehaviorNodeDefinition root)
        => _root = root ?? throw new ArgumentNullException(nameof(root));

    public BehaviorTree Build()
    {
        var nodes = new List<BehaviorTreeNode>();
        CompileNode(_root, nodes);
        return new BehaviorTree(nodes.ToArray());
    }

    public static BehaviorTree Build(BehaviorNodeDefinition root)
        => new BehaviorTreeBuilder(root).Build();

    private static void CompileNode(BehaviorNodeDefinition definition, List<BehaviorTreeNode> nodes)
    {
        ValidateChildCount(definition);

        int index = nodes.Count;
        nodes.Add(default);

        foreach (BehaviorNodeDefinition child in definition.Children)
        {
            CompileNode(child, nodes);
        }

        nodes[index] = new BehaviorTreeNode(definition.Factory, nodes.Count);
    }

    /// <summary>
    /// A leaf with a child or a decorator without exactly one otherwise misbehaves SILENTLY —
    /// traversal is index math that never visits the impossible child. Checked against the node's
    /// own <c>[Builder]</c> cardinality; a node without the attribute claims nothing, and
    /// composites accept any count.
    /// </summary>
    private static void ValidateChildCount(BehaviorNodeDefinition definition)
    {
        int childCount = definition.Children.Count;
        switch (definition.Factory.Cardinality)
        {
            case NodeCardinality.Leaf when childCount != 0:
                throw new InvalidOperationException(
                    $"Leaf node '{definition.NodeType.FullName}' cannot have children, got "
                    + $"{childCount}.");
            case NodeCardinality.Decorator when childCount != 1:
                throw new InvalidOperationException(
                    $"Decorator node '{definition.NodeType.FullName}' must have exactly one "
                    + $"child, got {childCount}.");
        }
    }
}

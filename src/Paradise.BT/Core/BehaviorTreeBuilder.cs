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
    /// A leaf with a child or a decorator without exactly one is a wiring mistake that otherwise
    /// SILENTLY misbehaves — traversal is index math, so an inverter's second child is simply
    /// never ticked. The generated builders make such trees hard to write; this catches the trees
    /// assembled from raw definitions, where nothing else would.
    ///
    /// The claim checked is the node's own <c>[Builder]</c> cardinality; a node without the
    /// attribute claims nothing and is not checked. Composites accept any count, as EntitiesBT's
    /// do.
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

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
        ValidateDefinition(_root);

        var nodes = new List<BehaviorTreeNode>();
        CompileNode(_root, nodes);
        return new BehaviorTree(nodes.ToArray());
    }

    public static BehaviorTree Build(BehaviorNodeDefinition root)
        => new BehaviorTreeBuilder(root).Build();

    private static void CompileNode(BehaviorNodeDefinition definition, List<BehaviorTreeNode> nodes)
    {
        int index = nodes.Count;
        nodes.Add(default);

        foreach (BehaviorNodeDefinition child in definition.Children)
        {
            CompileNode(child, nodes);
        }

        nodes[index] = new BehaviorTreeNode(definition.Factory, nodes.Count);
    }

    private static void ValidateDefinition(BehaviorNodeDefinition definition)
    {
        int childCount = definition.Children.Count;
        switch (definition.NodeTypeKind)
        {
            case BehaviorNodeType.Action when childCount != 0:
                ThrowHelper.ThrowInvalidNodeDefinition(definition.NodeType, definition.NodeTypeKind, childCount);
                break;
            case BehaviorNodeType.Decorate when childCount != 1:
                ThrowHelper.ThrowInvalidNodeDefinition(definition.NodeType, definition.NodeTypeKind, childCount);
                break;
        }

        foreach (BehaviorNodeDefinition child in definition.Children)
        {
            ValidateDefinition(child);
        }
    }
}

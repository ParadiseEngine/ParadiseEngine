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
        int index = nodes.Count;
        nodes.Add(default);

        foreach (BehaviorNodeDefinition child in definition.Children)
        {
            CompileNode(child, nodes);
        }

        nodes[index] = new BehaviorTreeNode(definition.Factory, nodes.Count);
    }

}

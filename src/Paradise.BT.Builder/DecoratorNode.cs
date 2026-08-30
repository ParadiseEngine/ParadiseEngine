namespace Paradise.BT.Builder;

public class DecoratorNode<T>(T data, BTreeNode child) : BTreeNode<T>(data)
    where T : struct, INode
{
    private readonly BTreeNode _child = child ?? throw new ArgumentNullException(nameof(child));

    internal sealed override int ChildCount => 1;

    internal sealed override void CompileChildren(List<BehaviorTreeNode> nodes)
        => _child.Compile(nodes);
}

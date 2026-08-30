namespace Paradise.BT.Builder;

public class LeafNode<T>(T data) : BTreeNode<T>(data)
    where T : struct, INode
{
    internal sealed override int ChildCount => 0;

    internal sealed override void CompileChildren(List<BehaviorTreeNode> nodes)
    {
    }
}

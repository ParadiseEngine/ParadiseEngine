namespace Paradise.BT.Builder;

public class DecoratorNode<T> : BTreeNode where T : struct, INodeData
{
    private readonly T _data;
    private readonly BTreeNode _child;

    public DecoratorNode(T data, BTreeNode child)
    {
        _data = data;
        _child = child;
    }

    protected internal override BehaviorNodeDefinition ToDefinition()
        => BehaviorNodes.Node(_data, _child.ToDefinition());
}

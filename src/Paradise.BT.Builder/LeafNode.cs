namespace Paradise.BT.Builder;

public class LeafNode<T> : BTreeNode where T : struct, INodeData
{
    private readonly T _data;

    public LeafNode(T data) => _data = data;

    protected internal override BehaviorNodeDefinition ToDefinition()
        => BehaviorNodes.Node(_data);
}

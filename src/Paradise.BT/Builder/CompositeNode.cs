namespace Paradise.BT.Builder;

public class CompositeNode<T> : BTreeNode where T : struct, INodeData
{
    private readonly T _data;
    private readonly BTreeNode[] _children;

    public CompositeNode(T data, params BTreeNode[] children)
    {
        _data = data;
        _children = children;
    }

    protected internal override BehaviorNodeDefinition ToDefinition()
    {
        var childDefs = new BehaviorNodeDefinition[_children.Length];
        for (int i = 0; i < _children.Length; i++)
            childDefs[i] = _children[i].ToDefinition();
        return BehaviorNodes.Node(_data, childDefs);
    }
}

namespace Paradise.BT.Builder;

public class CompositeNode<T> : BTreeNode where T : struct, INodeData
{
    private readonly T _data;
    private readonly BTreeNode[] _children;

    /// <summary>
    /// A span rather than an array, for ownership rather than allocation — the count is the same
    /// either way. The array form stored the CALLER's array, so writing to it afterwards rewired
    /// an already-built tree. A span cannot be retained, so the copy is forced.
    /// </summary>
    public CompositeNode(T data, params ReadOnlySpan<BTreeNode> children)
    {
        _data = data;
        _children = children.ToArray();
    }

    protected internal override BehaviorNodeDefinition ToDefinition()
    {
        var childDefs = new BehaviorNodeDefinition[_children.Length];
        for (int i = 0; i < _children.Length; i++)
        {
            childDefs[i] = _children[i].ToDefinition();
        }

        return BehaviorNodes.Node(_data, childDefs);
    }
}

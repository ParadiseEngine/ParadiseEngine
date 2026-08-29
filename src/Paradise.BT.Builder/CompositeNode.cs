namespace Paradise.BT.Builder;

public class CompositeNode<T> : BTreeNode where T : struct, INodeData
{
    private readonly T _data;
    private readonly BTreeNode[] _children;

    /// <summary>
    /// <c>params ReadOnlySpan</c> rather than <c>params BTreeNode[]</c>, for a reason that is
    /// about ownership rather than allocation — the count is the same either way, since the
    /// children have to be kept.
    ///
    /// The array form stored the CALLER's array. For the usual literal call the compiler mints a
    /// fresh one and nothing can reach it, but <c>new Sequence(data, myArray)</c> handed the tree a
    /// reference to <c>myArray</c>: writing to it afterwards silently rewired an already-built
    /// tree. A span cannot be retained, so copying is forced and the tree owns its children.
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

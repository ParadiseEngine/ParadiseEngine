namespace Paradise.BT.Builder;

public class CompositeNode<T> : BTreeNode<T> where T : struct, INode
{
    private readonly BTreeNode[] _children;

    /// <summary>A span rather than an array so the copy is forced — a stored caller array could
    /// be rewired after the fact.</summary>
    public CompositeNode(T data, params ReadOnlySpan<BTreeNode> children)
        : base(data)
        => _children = children.ToArray();

    internal sealed override int ChildCount => _children.Length;

    internal sealed override void CompileChildren(List<INodeBuilder> nodes)
    {
        foreach (BTreeNode child in _children)
        {
            child.Compile(nodes);
        }
    }
}

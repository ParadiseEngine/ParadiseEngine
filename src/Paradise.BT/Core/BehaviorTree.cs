using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// Immutable compiled behavior tree.
/// </summary>
public sealed class BehaviorTree
{
    private readonly BehaviorTreeNode[] _nodes;

    internal BehaviorTree(BehaviorTreeNode[] nodes)
    {
        _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        if (_nodes.Length == 0)
        {
            throw new ArgumentException("Behavior tree must contain at least one node.", nameof(nodes));
        }
    }

    public int Count => _nodes.Length;

    public BehaviorTreeInstance CreateInstance(Blackboard blackboard = default)
        => new BehaviorTreeInstance(this, blackboard);

    public ManagedBlobAssetReference<BehaviorTreeBlob> Serialize()
        => BehaviorTreeBlobSerializer.Serialize(this);

    public byte[] SerializeToBytes()
        => BehaviorTreeBlobSerializer.SerializeToBytes(this);

    public Type GetNodeType(int nodeIndex)
    {
        ThrowHelper.ThrowIfNodeIndexOutOfRange(nodeIndex, Count);
        return _nodes[nodeIndex].Factory.NodeType;
    }

    public BehaviorNodeType GetNodeBehaviorType(int nodeIndex)
    {
        ThrowHelper.ThrowIfNodeIndexOutOfRange(nodeIndex, Count);
        return _nodes[nodeIndex].Factory.NodeTypeKind;
    }

    public int GetEndIndex(int nodeIndex)
    {
        ThrowHelper.ThrowIfNodeIndexOutOfRange(nodeIndex, Count);
        return _nodes[nodeIndex].EndIndex;
    }

    internal BehaviorTreeNode GetCompiledNode(int nodeIndex)
    {
        ThrowHelper.ThrowIfNodeIndexOutOfRange(nodeIndex, Count);
        return _nodes[nodeIndex];
    }
}

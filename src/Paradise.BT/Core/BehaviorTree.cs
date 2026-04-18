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

    /// <summary>
    /// Creates a new instance using the default <see cref="Blackboard"/> implementation. Returns the
    /// non-generic <see cref="BehaviorTreeInstance"/> so existing consumers that reference the type by
    /// name keep compiling.
    /// </summary>
    public BehaviorTreeInstance CreateInstance(Blackboard blackboard = default)
        => new BehaviorTreeInstance(this, blackboard);

    /// <summary>
    /// Creates a new instance backed by a caller-supplied <typeparamref name="TBlackboard"/>. The struct
    /// constraint keeps tick dispatch allocation-free and lets the JIT specialise per blackboard type.
    /// </summary>
    public BehaviorTreeInstance<TBlackboard> CreateInstance<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard
        => new BehaviorTreeInstance<TBlackboard>(this, blackboard);

    public ManagedBlobAssetReference<BehaviorTreeBlob> Serialize()
        => BehaviorTreeBlobSerializer.Serialize(this);

    public byte[] SerializeToBytes()
        => BehaviorTreeBlobSerializer.SerializeToBytes(this);

    public Type GetNodeType(int nodeIndex)
    {
        ThrowHelper.ThrowIfNodeIndexOutOfRange(nodeIndex, Count);
        return _nodes[nodeIndex].Factory.NodeType;
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

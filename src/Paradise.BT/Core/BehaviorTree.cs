using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// Immutable compiled behavior tree.
/// </summary>
public sealed class BehaviorTree : IDisposable
{
    private readonly BehaviorTreeNode[] _nodes;
    private BehaviorTreeLayout? _layout;

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
    /// Creates a new instance backed by a caller-supplied <typeparamref name="TBlackboard"/>. The struct
    /// constraint keeps tick dispatch allocation-free and lets the JIT specialise per blackboard type.
    /// Concrete blackboard implementations (e.g. the reference <c>Blackboard</c> struct) live outside the
    /// core package; consumers typically reach for the <c>Paradise.BT.Nodes</c> overload.
    /// </summary>
    public BehaviorTreeInstance<TBlackboard> CreateInstance<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard
        => new BehaviorTreeInstance<TBlackboard>(this, blackboard);

    /// <summary>
    /// The flattened form every instance ticks against, built on first use and shared by all of
    /// them — the topology, the node type ids and the authored defaults do not vary per instance.
    ///
    /// It owns a native allocation, which is why this type is <see cref="IDisposable"/>. A tree
    /// that is never instanced never builds one. Every node type must be registered with
    /// <see cref="NodeTypeRegistry"/> first; the BT generator emits that registration per
    /// assembly, so in practice this only bites a node type the generator cannot see.
    /// </summary>
    internal BehaviorTreeLayout Layout => _layout ??= BehaviorTreeLayout.Build(this);

    public void Dispose()
    {
        _layout?.Dispose();
        _layout = null;
        GC.SuppressFinalize(this);
    }

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

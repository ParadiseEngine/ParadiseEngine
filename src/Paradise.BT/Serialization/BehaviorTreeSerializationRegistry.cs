using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// Maps serialized node GUIDs back to concrete node types during behavior tree deserialization.
/// </summary>
public sealed class BehaviorTreeSerializationRegistry
{
    private readonly Dictionary<Guid, IRegisteredNode> _nodes = new();

    public BehaviorTreeSerializationRegistry(bool includeBuiltInNodes = true)
    {
        if (includeBuiltInNodes)
        {
            RegisterBuiltInNodes();
        }
    }

    public BehaviorTreeSerializationRegistry Register<TNodeData>()
        where TNodeData : unmanaged, INodeData
    {
        IRegisteredNode registeredNode = RegisteredNode<TNodeData>.Instance;
        if (_nodes.TryGetValue(registeredNode.NodeGuid, out IRegisteredNode? existing)
            && existing.NodeType != registeredNode.NodeType)
        {
            throw new InvalidOperationException(
                $"Node GUID '{registeredNode.NodeGuid}' is already registered for '{existing.NodeType.FullName}'.");
        }

        _nodes[registeredNode.NodeGuid] = registeredNode;
        return this;
    }

    public BehaviorTreeSerializationRegistry RegisterBuiltInNodes()
        => Register<SequenceNode>()
            .Register<SelectorNode>()
            .Register<ParallelNode>()
            .Register<RepeatTimesNode>()
            .Register<RepeatForeverNode>()
            .Register<InverterNode>()
            .Register<SucceederNode>()
            .Register<DelayTimerNode>()
            .Register<SuccessNode>()
            .Register<FailedNode>()
            .Register<RunningNode>();

    public static BehaviorTreeSerializationRegistry CreateBuiltIn()
        => new();

    internal IRuntimeNodeFactory CreateFactory(ref BehaviorTreeBlobNode node)
    {
        if (_nodes.TryGetValue(node.NodeGuid, out IRegisteredNode? registeredNode))
        {
            return registeredNode.CreateFactory(ref node);
        }

        throw new InvalidOperationException(
            $"Node GUID '{node.NodeGuid}' is not registered for behavior tree deserialization. "
            + $"Call {nameof(Register)}<TNodeData>() on {nameof(BehaviorTreeSerializationRegistry)} first.");
    }

    private interface IRegisteredNode
    {
        Guid NodeGuid { get; }

        Type NodeType { get; }

        IRuntimeNodeFactory CreateFactory(ref BehaviorTreeBlobNode node);
    }

    private sealed class RegisteredNode<TNodeData> : IRegisteredNode
        where TNodeData : unmanaged, INodeData
    {
        public static readonly RegisteredNode<TNodeData> Instance = new();

        public Guid NodeGuid => typeof(TNodeData).GetNodeGuid();

        public Type NodeType => typeof(TNodeData);

        public IRuntimeNodeFactory CreateFactory(ref BehaviorTreeBlobNode node)
        {
            ref BlobPtrAny defaultDataPtr = ref node.DefaultData;
            TNodeData defaultData = defaultDataPtr.GetValue<TNodeData>();
            return new RuntimeNodeFactory<TNodeData>(defaultData, new BehaviorNodeMetadata(NodeGuid));
        }
    }
}

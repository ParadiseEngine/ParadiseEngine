namespace Paradise.BT;

/// <summary>
/// Managed runtime blob implementing the exact EntitiesBT <see cref="INodeBlob"/> contract.
/// </summary>
public struct NodeBlob : INodeBlob, IRuntimeNodeProvider, INodeDataAccessor
{
    private readonly NodeBlobStorage? _storage;

    private NodeBlob(NodeBlobStorage storage)
    {
        _storage = storage;
    }

    public int RuntimeId => Storage.RuntimeId;

    public int Count => Storage.Nodes.Length;

    public int GetTypeId(int nodeIndex) => Storage.Nodes[nodeIndex].TypeId;

    public int GetEndIndex(int nodeIndex) => Storage.EndIndices[nodeIndex];

    public int GetNodeDataSize(int startNodeIndex, int count = 1)
        => throw new NotSupportedException("Raw node data sizing is not supported by Paradise.BT's managed runtime blob.");

    public NodeState GetState(int nodeIndex) => Storage.States[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => Storage.States[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => Array.Clear(Storage.States, index, count);

    public IntPtr GetDefaultDataPtr(int nodeIndex)
        => throw new NotSupportedException("Pointer-based node data access is not supported by Paradise.BT's managed runtime blob.");

    public IntPtr GetRuntimeDataPtr(int nodeIndex)
        => throw new NotSupportedException("Pointer-based node data access is not supported by Paradise.BT's managed runtime blob.");

    public IntPtr GetDefaultScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope value pointers are not supported by Paradise.BT's managed runtime blob.");

    public IntPtr GetRuntimeScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope value pointers are not supported by Paradise.BT's managed runtime blob.");

    internal static NodeBlob Create(BehaviorTree tree)
    {
        int runtimeId = Environment.TickCount ^ tree.GetHashCode();
        var nodes = new IRuntimeNode[tree.Count];
        var endIndices = new int[tree.Count];
        var states = new NodeState[tree.Count];

        for (int i = 0; i < tree.Count; i++)
        {
            BehaviorTreeNode compiledNode = tree.GetCompiledNode(i);
            nodes[i] = compiledNode.Factory.CreateRuntimeNode();
            endIndices[i] = compiledNode.EndIndex;
        }

        return new NodeBlob(new NodeBlobStorage(runtimeId, nodes, endIndices, states));
    }

    IRuntimeNode IRuntimeNodeProvider.GetRuntimeNode(int nodeIndex) => Storage.Nodes[nodeIndex];

    void IRuntimeNodeProvider.ResetRuntimeData(int index, int count)
    {
        for (int i = index; i < index + count; i++)
        {
            Storage.Nodes[i].CopyDefaultToRuntime();
        }
    }

    ref T INodeDataAccessor.GetRuntimeNodeData<T>(int index)
    {
        if (Storage.Nodes[index] is IRuntimeNodeDataAccess accessor)
        {
            return ref accessor.GetRuntimeData<T>();
        }

        throw new InvalidOperationException($"Node at index {index} is not of type '{typeof(T).FullName}'.");
    }

    ref T INodeDataAccessor.GetDefaultNodeData<T>(int index)
    {
        if (Storage.Nodes[index] is IRuntimeNodeDataAccess accessor)
        {
            return ref accessor.GetDefaultData<T>();
        }

        throw new InvalidOperationException($"Node at index {index} is not of type '{typeof(T).FullName}'.");
    }

    private NodeBlobStorage Storage => _storage ?? throw new InvalidOperationException("NodeBlob is not initialized.");

    private sealed class NodeBlobStorage
    {
        public NodeBlobStorage(int runtimeId, IRuntimeNode[] nodes, int[] endIndices, NodeState[] states)
        {
            RuntimeId = runtimeId;
            Nodes = nodes;
            EndIndices = endIndices;
            States = states;
        }

        public int RuntimeId { get; }

        public IRuntimeNode[] Nodes { get; }

        public int[] EndIndices { get; }

        public NodeState[] States { get; }
    }
}

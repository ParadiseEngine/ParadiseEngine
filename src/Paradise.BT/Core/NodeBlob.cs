namespace Paradise.BT;

/// <summary>
/// Managed runtime blob implementing the exact EntitiesBT <see cref="INodeBlob"/> contract.
/// </summary>
public struct NodeBlob : INodeBlob
{
    private readonly NodeBlobStorage? _storage;

    private NodeBlob(NodeBlobStorage storage)
    {
        _storage = storage;
    }

    public int RuntimeId => Storage.RuntimeId;

    public int Count => Storage.Data.Length;

    public int GetTypeId(int nodeIndex) => Storage.Factories[nodeIndex].TypeId;

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
        var factories = new IRuntimeNodeFactory[tree.Count];
        var data = new object[tree.Count];
        var endIndices = new int[tree.Count];
        var states = new NodeState[tree.Count];

        for (int i = 0; i < tree.Count; i++)
        {
            BehaviorTreeNode compiledNode = tree.GetCompiledNode(i);
            factories[i] = compiledNode.Factory;
            data[i] = compiledNode.Factory.CreateBoxedData();
            endIndices[i] = compiledNode.EndIndex;
        }

        return new NodeBlob(new NodeBlobStorage(runtimeId, factories, data, endIndices, states));
    }

    // Direct instance methods rather than interface implementations: VirtualMachine reaches them
    // through a `ref NodeBlob`, and converting a struct to an interface boxes — which used to
    // happen on every node of every tick. It also cannot be done at all from a generic whose blob
    // type `allows ref struct`, and every caller here is now such a generic.
    internal NodeState Tick<TNodeBlob, TBlackboard>(
        int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Storage.Factories[index].Tick(Storage.Data[index], index, ref blob, ref bb);

    internal void Reset<TNodeBlob, TBlackboard>(
        int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Storage.Factories[index].Reset(index, ref blob, ref bb);

    internal void ResetRuntimeData(int index, int count)
    {
        for (int i = index; i < index + count; i++)
        {
            Storage.Factories[i].RestoreDefault(Storage.Data[i]);
        }
    }

    internal ref T RuntimeNodeData<T>(int index) where T : struct
        => ref Storage.Factories[index].DataRef<T>(Storage.Data[index]);

    internal ref T DefaultNodeData<T>(int index) where T : struct
        => ref Storage.Factories[index].DefaultRef<T>();

    private NodeBlobStorage Storage => _storage ?? throw new InvalidOperationException("NodeBlob is not initialized.");

    private sealed class NodeBlobStorage
    {
        public NodeBlobStorage(
            int runtimeId, IRuntimeNodeFactory[] factories, object[] data, int[] endIndices,
            NodeState[] states)
        {
            RuntimeId = runtimeId;
            Factories = factories;
            Data = data;
            EndIndices = endIndices;
            States = states;
        }

        public int RuntimeId { get; }

        /// <summary>One per node, shared with the tree — the dispatch half, which does not vary
        /// per instance.</summary>
        public IRuntimeNodeFactory[] Factories { get; }

        /// <summary>The boxed node data, one per node. The only genuinely per-instance state.</summary>
        public object[] Data { get; }

        public int[] EndIndices { get; }

        public NodeState[] States { get; }
    }
}

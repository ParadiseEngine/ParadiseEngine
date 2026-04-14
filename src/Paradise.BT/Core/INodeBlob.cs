namespace Paradise.BT;

/// <summary>
/// Exact node blob contract used by EntitiesBT-style nodes.
/// </summary>
public interface INodeBlob
{
    int RuntimeId { get; }

    int Count { get; }

    int GetTypeId(int nodeIndex);

    int GetEndIndex(int nodeIndex);

    int GetNodeDataSize(int startNodeIndex, int count = 1);

    NodeState GetState(int nodeIndex);

    void SetState(int nodeIndex, NodeState state);

    void ResetStates(int index, int count = 1);

    IntPtr GetDefaultDataPtr(int nodeIndex);

    IntPtr GetRuntimeDataPtr(int nodeIndex);

    IntPtr GetDefaultScopeValuePtr(int offset);

    IntPtr GetRuntimeScopeValuePtr(int offset);
}

internal interface IRuntimeNodeProvider
{
    IRuntimeNode GetRuntimeNode(int nodeIndex);

    void ResetRuntimeData(int index, int count = 1);
}

internal interface INodeDataAccessor
{
    ref T GetRuntimeNodeData<T>(int index) where T : struct;

    ref T GetDefaultNodeData<T>(int index) where T : struct;
}

/// <summary>
/// EntitiesBT-compatible blob helpers implemented for Paradise.BT's managed runtime blob.
/// </summary>
public static class NodeBlobExtensions
{
    public static int FirstOrDefaultChildIndex<TNodeBlob>(this ref TNodeBlob blob, int parentIndex, Predicate<NodeState> predicate)
        where TNodeBlob : struct, INodeBlob
    {
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        while (childIndex < endIndex)
        {
            if (predicate(blob.GetState(childIndex)))
            {
                return childIndex;
            }

            childIndex = blob.GetEndIndex(childIndex);
        }

        return default;
    }

    public static int ParentIndex<TNodeBlob>(this ref TNodeBlob blob, int childIndex)
        where TNodeBlob : struct, INodeBlob
    {
        int endIndex = blob.GetEndIndex(childIndex);
        for (int i = childIndex - 1; i >= 0; i--)
        {
            if (blob.GetEndIndex(i) >= endIndex)
            {
                return i;
            }
        }

        return -1;
    }

    public static void ResetRuntimeData<TNodeBlob>(this ref TNodeBlob blob, int index, int count = 1)
        where TNodeBlob : struct, INodeBlob
    {
        if (blob is IRuntimeNodeProvider provider)
        {
            provider.ResetRuntimeData(index, count);
            return;
        }

        throw new NotSupportedException("Runtime node data reset is only supported by Paradise.BT NodeBlob instances.");
    }

    public static ref T GetNodeData<T, TNodeBlob>(this ref TNodeBlob blob, int index)
        where T : struct
        where TNodeBlob : struct, INodeBlob
    {
        if (blob is INodeDataAccessor accessor)
        {
            return ref accessor.GetRuntimeNodeData<T>(index);
        }

        throw new NotSupportedException("Runtime node data access is only supported by Paradise.BT NodeBlob instances.");
    }

    public static ref T GetNodeDefaultData<T, TNodeBlob>(this ref TNodeBlob blob, int index)
        where T : struct
        where TNodeBlob : struct, INodeBlob
    {
        if (blob is INodeDataAccessor accessor)
        {
            return ref accessor.GetDefaultNodeData<T>(index);
        }

        throw new NotSupportedException("Default node data access is only supported by Paradise.BT NodeBlob instances.");
    }
}

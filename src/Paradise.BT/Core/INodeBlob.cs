using System.Runtime.CompilerServices;

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
        where TNodeBlob : struct, INodeBlob, allows ref struct
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
        where TNodeBlob : struct, INodeBlob, allows ref struct
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

    /// <summary>Restore a run of nodes' authored data. The managed blob re-copies each boxed
    /// node's default; a byte-backed one copies its default region over its runtime one.</summary>
    public static unsafe void ResetRuntimeData<TNodeBlob>(this ref TNodeBlob blob, int index, int count = 1)
        where TNodeBlob : struct, INodeBlob, allows ref struct
    {
        if (typeof(TNodeBlob) == typeof(NodeBlob))
        {
            Unsafe.As<TNodeBlob, NodeBlob>(ref blob).ResetRuntimeData(index, count);
            return;
        }

        int size = blob.GetNodeDataSize(index, count);
        if (size > 0)
        {
            new ReadOnlySpan<byte>((void*)blob.GetDefaultDataPtr(index), size)
                .CopyTo(new Span<byte>((void*)blob.GetRuntimeDataPtr(index), size));
        }
    }

    /// <summary>A node's live data, typed. The <c>blob is INodeDataAccessor</c> test this used to
    /// open with cannot survive <c>allows ref struct</c>, and boxed on every call besides.</summary>
    public static unsafe ref T GetNodeData<T, TNodeBlob>(this ref TNodeBlob blob, int index)
        where T : struct
        where TNodeBlob : struct, INodeBlob, allows ref struct
        => ref typeof(TNodeBlob) == typeof(NodeBlob)
            ? ref Unsafe.As<TNodeBlob, NodeBlob>(ref blob).RuntimeNodeData<T>(index)
            : ref Unsafe.AsRef<T>((void*)blob.GetRuntimeDataPtr(index));

    /// <inheritdoc cref="GetNodeData{T, TNodeBlob}"/>
    public static unsafe ref T GetNodeDefaultData<T, TNodeBlob>(this ref TNodeBlob blob, int index)
        where T : struct
        where TNodeBlob : struct, INodeBlob, allows ref struct
        => ref typeof(TNodeBlob) == typeof(NodeBlob)
            ? ref Unsafe.As<TNodeBlob, NodeBlob>(ref blob).DefaultNodeData<T>(index)
            : ref Unsafe.AsRef<T>((void*)blob.GetDefaultDataPtr(index));
}

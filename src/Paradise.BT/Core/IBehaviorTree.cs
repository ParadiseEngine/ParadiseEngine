using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The per-instance state of a running tree — what each node returned last tick, and each node's
/// live data. <b>Data is reached by <c>ref byte</c>, not by pointer</b>: a managed ref survives
/// GC compaction, so a blob may sit over plain arrays with no pinning.
/// </summary>
public interface IBehaviorTree
{
    /// <summary>The node's durable type identity — what <see cref="VirtualMachine"/> hands to
    /// <see cref="NodeTypeRegistry"/> for dispatch.</summary>
    Guid GetTypeGuid(int nodeIndex);

    int GetEndIndex(int nodeIndex);

    /// <summary>How many bytes <paramref name="count"/> nodes occupy from
    /// <paramref name="startNodeIndex"/>, including the padding that keeps each node aligned.</summary>
    int GetNodeDataSize(int startNodeIndex, int count = 1);

    NodeState GetState(int nodeIndex);

    void SetState(int nodeIndex, NodeState state);

    void ResetStates(int index, int count = 1);

    /// <summary>The authored default for a node — shared, and never written through.</summary>
    ref byte DefaultData(int nodeIndex);

    /// <summary>A node's live data. Ticking writes through this, which is what makes a timer
    /// count down.</summary>
    ref byte RuntimeData(int nodeIndex);
}

/// <summary>
/// Blob traversal and data helpers.
/// </summary>
public static class BehaviorTreeExtensions
{
    public static int FirstOrDefaultChildIndex<TBehaviorTree>(this TBehaviorTree blob, int parentIndex, Predicate<NodeState> predicate)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
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

    public static int ParentIndex<TBehaviorTree>(this TBehaviorTree blob, int childIndex)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
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

    /// <summary>Restore a run of nodes' authored data — what restarts a timer on reset. One copy,
    /// because nodes are laid out contiguously.</summary>
    public static void ResetRuntimeData<TBehaviorTree>(this TBehaviorTree blob, int index, int count = 1)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
    {
        int size = blob.GetNodeDataSize(index, count);
        if (size > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(ref blob.DefaultData(index), size)
                .CopyTo(MemoryMarshal.CreateSpan(ref blob.RuntimeData(index), size));
        }
    }

    /// <summary>A node's live data, typed.</summary>
    public static ref T GetNodeData<T, TBehaviorTree>(this TBehaviorTree blob, int index)
        where T : struct
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        => ref Unsafe.As<byte, T>(ref blob.RuntimeData(index));

    /// <inheritdoc cref="GetNodeData{T, TBehaviorTree}"/>
    public static ref T GetNodeDefaultData<T, TBehaviorTree>(this TBehaviorTree blob, int index)
        where T : struct
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        => ref Unsafe.As<byte, T>(ref blob.DefaultData(index));
}

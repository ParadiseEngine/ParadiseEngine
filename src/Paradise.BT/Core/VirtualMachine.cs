using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Exact VM entrypoints shaped like EntitiesBT, over either of the library's two node blobs.
///
/// <b>Two storage strategies, one set of nodes.</b> <see cref="NodeBlob"/> keeps a boxed
/// <c>RuntimeNode&lt;T&gt;</c> per node and knows how to tick it; <see cref="UnmanagedNodeBlob"/>
/// keeps node data as bytes and looks the type up in <see cref="NodeTypeRegistry"/>. Every node
/// implementation, every composite and every helper in <see cref="NodeExtensions"/> is generic
/// over the blob and cannot tell which it got — which is what let the unmanaged blob be added
/// without touching a single node.
///
/// <b>The branch below is free.</b> <c>typeof(TNodeBlob) == typeof(NodeBlob)</c> is a comparison
/// of two type handles known at JIT time, and for a value-type <c>TNodeBlob</c> the JIT folds it
/// to a constant and drops the dead side. It replaces a <c>blob is IRuntimeNodeProvider</c> test
/// that BOXED THE BLOB ON EVERY SINGLE NODE TICK, so the managed path got faster by acquiring a
/// second implementation.
/// </summary>
public static class VirtualMachine
{
    public static NodeState Tick<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Tick(0, ref blob, ref bb);

    public static NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        NodeState state = typeof(TNodeBlob) == typeof(NodeBlob)
            ? Managed(ref blob).GetRuntimeNode(index).Tick(index, ref blob, ref bb)
            : TickByTypeId(index, ref blob, ref bb);
        blob.SetState(index, state);
        return state;
    }

    public static void Reset<TNodeBlob, TBlackboard>(int fromIndex, ref TNodeBlob blob, ref TBlackboard bb, int count = 1)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        blob.ResetStates(fromIndex, count);

        if (typeof(TNodeBlob) == typeof(NodeBlob))
        {
            Managed(ref blob).ResetRuntimeData(fromIndex, count);
            for (int i = fromIndex; i < fromIndex + count; i++)
            {
                Managed(ref blob).GetRuntimeNode(i).Reset(i, ref blob, ref bb);
            }

            return;
        }

        RestoreDefaultData(fromIndex, ref blob, count);
        for (int i = fromIndex; i < fromIndex + count; i++)
        {
            NodeTypeRegistry.Invoker(blob.GetTypeId(i))
                .Reset(ref RuntimeData(ref blob, i), i, ref blob, ref bb);
        }
    }

    public static void Reset<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        int count = blob.GetEndIndex(0);
        Reset(0, ref blob, ref bb, count);
    }

    private static NodeState TickByTypeId<TNodeBlob, TBlackboard>(
        int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => NodeTypeRegistry.Invoker(blob.GetTypeId(index))
            .Tick(ref RuntimeData(ref blob, index), index, ref blob, ref bb);

    /// <summary>
    /// Put a run of nodes' data back to what was authored — the byte-storage equivalent of
    /// <c>CopyDefaultToRuntime</c>, and what restarts a timer when its parent resets it.
    ///
    /// One copy rather than a loop, because a blob lays its nodes out contiguously and
    /// <see cref="INodeBlob.GetNodeDataSize"/> reports exactly the reserved span.
    /// </summary>
    private static unsafe void RestoreDefaultData<TNodeBlob>(
        int fromIndex, ref TNodeBlob blob, int count)
        where TNodeBlob : struct, INodeBlob, allows ref struct
    {
        int size = blob.GetNodeDataSize(fromIndex, count);
        if (size <= 0)
        {
            return;
        }

        new ReadOnlySpan<byte>((void*)blob.GetDefaultDataPtr(fromIndex), size)
            .CopyTo(new Span<byte>((void*)blob.GetRuntimeDataPtr(fromIndex), size));
    }

    private static unsafe ref byte RuntimeData<TNodeBlob>(ref TNodeBlob blob, int index)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        => ref Unsafe.AsRef<byte>((void*)blob.GetRuntimeDataPtr(index));

    /// <summary>
    /// Reinterpret the blob as the managed one. Only ever called on the branch where
    /// <c>typeof(TNodeBlob) == typeof(NodeBlob)</c> already holds, so this is a no-op
    /// reinterpretation rather than a conversion — and crucially not a box.
    /// </summary>
    private static ref NodeBlob Managed<TNodeBlob>(ref TNodeBlob blob)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        => ref Unsafe.As<TNodeBlob, NodeBlob>(ref blob);
}

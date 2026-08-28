using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Exact VM entrypoints shaped like EntitiesBT, over either node blob.
///
/// Two storage strategies, one set of nodes: <see cref="NodeBlob"/> boxes a node and ticks it,
/// <see cref="UnmanagedNodeBlob"/> keeps data as bytes and looks the type up in
/// <see cref="NodeTypeRegistry"/>. Nodes are generic over the blob and cannot tell which they got,
/// which is why the unmanaged blob needed no node changes.
///
/// The branch is free: <c>typeof(TNodeBlob) == typeof(NodeBlob)</c> folds to a constant at JIT
/// time. It replaces a <c>blob is IRuntimeNodeProvider</c> test that boxed on every node tick.
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
            ? Managed(ref blob).Tick(index, ref blob, ref bb)
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
                Managed(ref blob).Reset(i, ref blob, ref bb);
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
    /// Restore a run of nodes' authored data — the byte equivalent of <c>CopyDefaultToRuntime</c>,
    /// and what restarts a timer on reset. One copy, because nodes are laid out contiguously.
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

    /// <summary>Reinterpret as the managed blob. Only called where
    /// <c>typeof(TNodeBlob) == typeof(NodeBlob)</c> already holds, so it is a no-op — not a
    /// box.</summary>
    private static ref NodeBlob Managed<TNodeBlob>(ref TNodeBlob blob)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        => ref Unsafe.As<TNodeBlob, NodeBlob>(ref blob);
}

using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// VM entrypoints shaped like EntitiesBT, over the one node blob.
///
/// There used to be two blobs and a branch: a managed one that boxed a node per node per instance
/// and dispatched through an interface, and a byte-backed one that looks the node's type up in
/// <see cref="NodeTypeRegistry"/>. The managed one existed only to host nodes whose data was
/// managed — the delegate-backed pair — and those are gone, so this is one path over bytes.
/// </summary>
public static class VirtualMachine
{
    public static NodeState Tick<TNodeBlob, TBlackboard>(TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => Tick(0, blob, bb);

    public static NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState state = NodeTypeRegistry.Invoker(blob.GetTypeId(index))
            .Tick(ref blob.RuntimeData(index), index, blob, bb);
        blob.SetState(index, state);
        return state;
    }

    public static void Reset<TNodeBlob, TBlackboard>(int fromIndex, TNodeBlob blob, TBlackboard bb, int count = 1)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        blob.ResetStates(fromIndex, count);
        blob.ResetRuntimeData(fromIndex, count);

        for (int i = fromIndex; i < fromIndex + count; i++)
        {
            NodeTypeRegistry.Invoker(blob.GetTypeId(i)).Reset(
                ref blob.RuntimeData(i), i, blob, bb);
        }
    }

    public static void Reset<TNodeBlob, TBlackboard>(TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int count = blob.GetEndIndex(0);
        Reset(0, blob, bb, count);
    }
}

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
    public static NodeState Tick<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Tick(0, ref blob, ref bb);

    public static NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        NodeState state = NodeTypeRegistry.Invoker(blob.GetTypeId(index))
            .Tick(ref blob.RuntimeData(index), index, ref blob, ref bb);
        blob.SetState(index, state);
        return state;
    }

    public static void Reset<TNodeBlob, TBlackboard>(int fromIndex, ref TNodeBlob blob, ref TBlackboard bb, int count = 1)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        blob.ResetStates(fromIndex, count);
        blob.ResetRuntimeData(fromIndex, count);

        for (int i = fromIndex; i < fromIndex + count; i++)
        {
            NodeTypeRegistry.Invoker(blob.GetTypeId(i)).Reset(
                ref blob.RuntimeData(i), i, ref blob, ref bb);
        }
    }

    public static void Reset<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        int count = blob.GetEndIndex(0);
        Reset(0, ref blob, ref bb, count);
    }
}

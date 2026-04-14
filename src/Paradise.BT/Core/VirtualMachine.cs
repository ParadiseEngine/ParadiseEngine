namespace Paradise.BT;

/// <summary>
/// Exact VM entrypoints shaped like EntitiesBT, backed by Paradise.BT's managed node blob.
/// </summary>
public static class VirtualMachine
{
    public static NodeState Tick<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => Tick(0, ref blob, ref bb);

    public static NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        IRuntimeNodeProvider provider = GetProvider(blob);
        IRuntimeNode runtimeNode = provider.GetRuntimeNode(index);
        NodeState state = runtimeNode.Tick(index, ref blob, ref bb);
        blob.SetState(index, state);
        return state;
    }

    public static void Reset<TNodeBlob, TBlackboard>(int fromIndex, ref TNodeBlob blob, ref TBlackboard bb, int count = 1)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        blob.ResetStates(fromIndex, count);
        IRuntimeNodeProvider provider = GetProvider(blob);
        provider.ResetRuntimeData(fromIndex, count);
        for (int i = fromIndex; i < fromIndex + count; i++)
        {
            provider.GetRuntimeNode(i).Reset(i, ref blob, ref bb);
        }
    }

    public static void Reset<TNodeBlob, TBlackboard>(ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        int count = blob.GetEndIndex(0);
        Reset(0, ref blob, ref bb, count);
    }

    private static IRuntimeNodeProvider GetProvider<TNodeBlob>(TNodeBlob blob)
        where TNodeBlob : struct, INodeBlob
    {
        if (blob is IRuntimeNodeProvider provider)
        {
            return provider;
        }

        throw new NotSupportedException("VirtualMachine dispatch requires the Paradise.BT NodeBlob runtime implementation.");
    }
}

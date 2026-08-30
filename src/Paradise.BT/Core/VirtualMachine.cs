using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Tick and reset over a node blob: each node dispatches by its GUID through
/// <see cref="NodeTypeRegistry"/>, straight from its bytes.
/// </summary>
public static class VirtualMachine
{
    public static NodeState Tick<TBehaviorTree, TBlackboard>(TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => Tick(0, blob, bb);

    public static NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState state = NodeTypeRegistry.Invoker(blob.GetTypeGuid(index))
            .Tick(ref blob.RuntimeData(index), index, blob, bb);
        blob.SetState(index, state);
        return state;
    }

    public static void Reset<TBehaviorTree, TBlackboard>(int fromIndex, TBehaviorTree blob, TBlackboard bb, int count = 1)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        blob.ResetStates(fromIndex, count);
        blob.ResetRuntimeData(fromIndex, count);

        for (int i = fromIndex; i < fromIndex + count; i++)
        {
            NodeTypeRegistry.Invoker(blob.GetTypeGuid(i)).Reset(
                ref blob.RuntimeData(i), i, blob, bb);
        }
    }

    public static void Reset<TBehaviorTree, TBlackboard>(TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int count = blob.GetEndIndex(0);
        Reset(0, blob, bb, count);
    }
}

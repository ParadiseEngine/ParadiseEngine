namespace Paradise.BT;

/// <summary>
/// Exact child traversal helpers modeled after EntitiesBT's node extension methods.
/// </summary>
public static class NodeExtensions
{
    public static void ResetChildren<TNodeBlob, TBlackboard>(this int parentIndex, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int firstChildIndex = parentIndex + 1;
        int lastChildIndex = blob.GetEndIndex(parentIndex);
        int childCount = lastChildIndex - firstChildIndex;
        VirtualMachine.Reset(firstChildIndex, blob, bb, childCount);
    }

    public static NodeState TickChildrenReturnLastOrDefault<TNodeBlob, TBlackboard>(
        this int parentIndex,
        TNodeBlob blob,
        TBlackboard bb,
        Predicate<NodeState> breakCheck)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, breakCheck, static state => !state.IsCompleted());

    public static NodeState TickChildrenReturnFirstOrDefault<TNodeBlob, TBlackboard>(
        this int parentIndex,
        TNodeBlob blob,
        TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, static _ => true, static state => !state.IsCompleted());

    public static NodeState TickChild<TNodeBlob, TBlackboard>(
        this int parentIndex,
        TNodeBlob blob,
        TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        return childIndex < endIndex ? VirtualMachine.Tick(childIndex, blob, bb) : 0;
    }

    private static NodeState TickChildrenReturnBreakOrDefault<TNodeBlob, TBlackboard>(
        int parentIndex,
        TNodeBlob blob,
        TBlackboard bb,
        Predicate<NodeState> breakCheck,
        Predicate<NodeState> tickCheck)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState lastState = 0;
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = blob.GetState(childIndex);
            NodeState currentState = tickCheck(previousState) ? VirtualMachine.Tick(childIndex, blob, bb) : 0;
            lastState = currentState == 0 ? previousState : currentState;
            if (breakCheck(lastState))
            {
                break;
            }

            childIndex = blob.GetEndIndex(childIndex);
        }

        return lastState;
    }
}

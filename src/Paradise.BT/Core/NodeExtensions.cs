namespace Paradise.BT;

public static class NodeExtensions
{
    public static void ResetChildren<TBehaviorTree, TBlackboard>(this int parentIndex, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int firstChildIndex = parentIndex + 1;
        int lastChildIndex = blob.GetEndIndex(parentIndex);
        int childCount = lastChildIndex - firstChildIndex;
        VirtualMachine.Reset(firstChildIndex, blob, bb, childCount);
    }

    public static NodeState TickChildrenReturnLastOrDefault<TBehaviorTree, TBlackboard>(
        this int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb,
        Predicate<NodeState> breakCheck)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, breakCheck, static state => !state.IsCompleted());

    public static NodeState TickChildrenReturnFirstOrDefault<TBehaviorTree, TBlackboard>(
        this int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, static _ => true, static state => !state.IsCompleted());

    public static NodeState TickChild<TBehaviorTree, TBlackboard>(
        this int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        return childIndex < endIndex ? VirtualMachine.Tick(childIndex, blob, bb) : NodeState.None;
    }

    private static NodeState TickChildrenReturnBreakOrDefault<TBehaviorTree, TBlackboard>(
        int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb,
        Predicate<NodeState> breakCheck,
        Predicate<NodeState> tickCheck)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState lastState = NodeState.None;
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = blob.GetState(childIndex);
            NodeState currentState = tickCheck(previousState)
                ? VirtualMachine.Tick(childIndex, blob, bb)
                : NodeState.None;
            lastState = currentState == NodeState.None ? previousState : currentState;
            if (breakCheck(lastState))
            {
                break;
            }

            childIndex = blob.GetEndIndex(childIndex);
        }

        return lastState;
    }
}

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
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, breakCheck);

    public static NodeState TickChildrenReturnFirstOrDefault<TBehaviorTree, TBlackboard>(
        this int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TickChildrenReturnBreakOrDefault(parentIndex, blob, bb, static _ => true);

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

    /// <summary>Ticks a repeating decorator's child as one iteration of its loop, first resetting
    /// the child's whole subtree when the previous iteration completed.</summary>
    /// <remarks>Composites skip children that already completed, so re-ticking a completed
    /// <c>Sequence</c> replays its old result without running anything: a repeat built on
    /// <see cref="TickChild"/> alone counts three iterations of <c>Sequence(a)</c> with one call to
    /// <c>a</c>. A Running child is resumed, not reset. A child that answers None is reset and
    /// ticked once more, unless this call already reset it. Reset hooks therefore run once per
    /// completed iteration, where they used to run only when a child answered None.</remarks>
    public static NodeState TickChildIteration<TBehaviorTree, TBlackboard>(
        this int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        int childIndex = parentIndex + 1;
        bool reset = childIndex < blob.GetEndIndex(parentIndex) && blob.GetState(childIndex).IsCompleted();
        if (reset)
        {
            parentIndex.ResetChildren(blob, bb);
        }

        NodeState state = parentIndex.TickChild(blob, bb);
        // A child that answers None straight after this call reset it would answer None again;
        // resetting twice would only run its reset hooks twice in one tick.
        if (state == NodeState.None && !reset)
        {
            parentIndex.ResetChildren(blob, bb);
            state = parentIndex.TickChild(blob, bb);
        }

        return state;
    }

    private static NodeState TickChildrenReturnBreakOrDefault<TBehaviorTree, TBlackboard>(
        int parentIndex,
        TBehaviorTree blob,
        TBlackboard bb,
        Predicate<NodeState> breakCheck)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState lastState = NodeState.None;
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = blob.GetState(childIndex);
            NodeState currentState = !previousState.IsCompleted()
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

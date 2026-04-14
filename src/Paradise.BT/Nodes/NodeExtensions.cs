namespace Paradise.BT;

/// <summary>
/// Exact child traversal helpers modeled after EntitiesBT's node extension methods.
/// </summary>
public static class NodeExtensions
{
    public static void ResetChildren<TNodeBlob, TBlackboard>(this int parentIndex, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        int firstChildIndex = parentIndex + 1;
        int lastChildIndex = blob.GetEndIndex(parentIndex);
        int childCount = lastChildIndex - firstChildIndex;
        VirtualMachine.Reset(firstChildIndex, ref blob, ref bb, childCount);
    }

    public static NodeState TickChildrenReturnLastOrDefault<TNodeBlob, TBlackboard>(
        this int parentIndex,
        ref TNodeBlob blob,
        ref TBlackboard bb,
        Predicate<NodeState> breakCheck)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => TickChildrenReturnBreakOrDefault(parentIndex, ref blob, ref bb, breakCheck, static state => !state.IsCompleted());

    public static NodeState TickChildrenReturnFirstOrDefault<TNodeBlob, TBlackboard>(
        this int parentIndex,
        ref TNodeBlob blob,
        ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => TickChildrenReturnBreakOrDefault(parentIndex, ref blob, ref bb, static _ => true, static state => !state.IsCompleted());

    public static NodeState TickChild<TNodeBlob, TBlackboard>(
        this int parentIndex,
        ref TNodeBlob blob,
        ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        return childIndex < endIndex ? VirtualMachine.Tick(childIndex, ref blob, ref bb) : 0;
    }

    private static NodeState TickChildrenReturnBreakOrDefault<TNodeBlob, TBlackboard>(
        int parentIndex,
        ref TNodeBlob blob,
        ref TBlackboard bb,
        Predicate<NodeState> breakCheck,
        Predicate<NodeState> tickCheck)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        NodeState currentState = 0;
        int endIndex = blob.GetEndIndex(parentIndex);
        int childIndex = parentIndex + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = blob.GetState(childIndex);
            currentState = tickCheck(previousState) ? VirtualMachine.Tick(childIndex, ref blob, ref bb) : 0;
            if (breakCheck(currentState == 0 ? previousState : currentState))
            {
                break;
            }

            childIndex = blob.GetEndIndex(childIndex);
        }

        return currentState;
    }
}

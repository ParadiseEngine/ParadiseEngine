namespace Paradise.BT.Nodes;

/// <summary>Repeats its child until the completion count is exhausted or a break state is reached.</summary>
/// <remarks>The child ticks before the count is checked, including when the initial count is zero.
/// Each iteration runs the child's whole subtree afresh: a completed child is reset before the next.</remarks>
[System.Runtime.InteropServices.Guid("76E27039-91C1-4DEF-AFEF-1EDDBAAE8CCE")]
[Builder("Repeat", NodeCardinality.Decorator)]
public struct RepeatTimesNode(int tickTimes, NodeState breakStates = NodeState.None) : INode
{
    public int TickTimes = tickTimes;
    public NodeState BreakStates = breakStates;

    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChildIteration(tree, bb);

        if (BreakStates.HasFlagFast(childState))
        {
            return childState;
        }

        if (childState.IsCompleted())
        {
            TickTimes--;
        }

        return TickTimes <= 0 ? NodeState.Success : NodeState.Running;
    }
}

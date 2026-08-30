namespace Paradise.BT.Nodes;

/// <summary>The primary constructor is the exposed surface the generated builder mirrors,
/// defaults included.</summary>
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
        NodeState childState = index.TickChild(tree, bb);
        if (childState == NodeState.None)
        {
            index.ResetChildren(tree, bb);
            childState = index.TickChild(tree, bb);
        }

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

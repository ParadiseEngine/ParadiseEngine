namespace Paradise.BT.Nodes;

/// <summary>The primary constructor is the exposed surface the generated builder mirrors,
/// defaults included.</summary>
[System.Runtime.InteropServices.Guid("76E27039-91C1-4DEF-AFEF-1EDDBAAE8CCE")]
[Builder("Repeat", NodeCardinality.Decorator)]
public struct RepeatTimesNode(int tickTimes, NodeState breakStates = NodeState.None) : INodeData
{
    public int TickTimes = tickTimes;
    public NodeState BreakStates = breakStates;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(blob, bb);
        if (childState == NodeState.None)
        {
            index.ResetChildren(blob, bb);
            childState = index.TickChild(blob, bb);
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

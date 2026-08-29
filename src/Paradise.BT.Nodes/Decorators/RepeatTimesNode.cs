namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("76E27039-91C1-4DEF-AFEF-1EDDBAAE8CCE")]
[Builder("Repeat", NodeCardinality.Decorator)]
public struct RepeatTimesNode : INodeData
{
    public int TickTimes;
    public NodeState BreakStates;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(blob, bb);
        if (childState == 0)
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

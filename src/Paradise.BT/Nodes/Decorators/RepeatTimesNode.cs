namespace Paradise.BT;

[System.Runtime.InteropServices.Guid("76E27039-91C1-4DEF-AFEF-1EDDBAAE8CCE")]
public struct RepeatTimesNode : INodeData
{
    public int TickTimes;
    public NodeState BreakStates;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        NodeState childState = index.TickChild(ref blob, ref bb);
        if (childState == 0)
        {
            index.ResetChildren(ref blob, ref bb);
            childState = index.TickChild(ref blob, ref bb);
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

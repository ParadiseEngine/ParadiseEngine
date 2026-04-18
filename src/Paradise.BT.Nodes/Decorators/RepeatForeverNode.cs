namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("A13666BD-48E3-414A-BD13-5C696F2EA87E")]
[Builder(NodeCardinality.Decorator)]
public struct RepeatForeverNode : INodeData
{
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

        return BreakStates.HasFlagFast(childState) ? childState : NodeState.Running;
    }
}

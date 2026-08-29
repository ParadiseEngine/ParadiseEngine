namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("A13666BD-48E3-414A-BD13-5C696F2EA87E")]
[Builder(NodeCardinality.Decorator)]
public struct RepeatForeverNode(NodeState breakStates) : INodeData
{
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

        return BreakStates.HasFlagFast(childState) ? childState : NodeState.Running;
    }
}

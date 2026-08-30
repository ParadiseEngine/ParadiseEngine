namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("A13666BD-48E3-414A-BD13-5C696F2EA87E")]
[Builder(NodeCardinality.Decorator)]
public struct RepeatForeverNode(NodeState breakStates) : INode
{
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

        return BreakStates.HasFlagFast(childState) ? childState : NodeState.Running;
    }
}

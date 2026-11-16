namespace Paradise.BT.Nodes;

/// <summary>Repeats its child until a break state is reached, running the child's whole subtree afresh each iteration.</summary>
[System.Runtime.InteropServices.Guid("A13666BD-48E3-414A-BD13-5C696F2EA87E")]
[Builder(NodeCardinality.Decorator)]
public struct RepeatForeverNode(NodeState breakStates) : INode
{
    public NodeState BreakStates = breakStates;

    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChildIteration(tree, bb);

        return BreakStates.HasFlagFast(childState) ? childState : NodeState.Running;
    }
}

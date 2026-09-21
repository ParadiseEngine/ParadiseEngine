namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("F17339E0-D401-451B-864B-007AD44E05A3")]
[Builder]
public struct RunningNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Running;
}

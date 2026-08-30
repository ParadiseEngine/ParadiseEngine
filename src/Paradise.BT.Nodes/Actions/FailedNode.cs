namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("AC5CB763-5F7A-4301-9670-D4E38A5557CB")]
[Builder("Failure")]
public struct FailedNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Failure;
}

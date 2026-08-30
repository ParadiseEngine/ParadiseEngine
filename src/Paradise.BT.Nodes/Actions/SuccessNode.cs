namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("A2E43D78-8993-4D0A-9CD0-70A98AAF9E8A")]
[Builder]
public struct SuccessNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Success;
}

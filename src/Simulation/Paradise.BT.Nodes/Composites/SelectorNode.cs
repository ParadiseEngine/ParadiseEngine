namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("BD4C1D8F-BA8E-4D74-9039-7D1E6010B058")]
[Builder(NodeCardinality.Composite)]
public struct SelectorNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => index.TickChildrenReturnLastOrDefault(tree, bb, static state => state.IsRunningOrSuccess());
}

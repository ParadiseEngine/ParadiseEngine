namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("54CA6411-0DEA-4820-A8AF-7D7B76BC3875")]
[Builder(NodeCardinality.Decorator)]
public struct InverterNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(tree, bb);
        if (childState == NodeState.Success) return NodeState.Failure;
        if (childState == NodeState.Failure) return NodeState.Success;
        return childState;
    }
}

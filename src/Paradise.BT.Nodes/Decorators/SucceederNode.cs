namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("8D789E4C-D4B8-41D9-A2CD-47C7024B1D51")]
[Builder(NodeCardinality.Decorator)]
public struct SucceederNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(tree, bb);
        return childState == NodeState.Failure ? NodeState.Success : childState;
    }
}

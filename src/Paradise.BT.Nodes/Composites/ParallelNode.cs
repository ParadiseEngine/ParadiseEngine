namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("A316D182-7D8C-4075-A46D-FEE08CAEEEAF")]
[Builder(NodeCardinality.Composite)]
public struct ParallelNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState flags = NodeState.None;
        int endIndex = tree.GetEndIndex(index);
        int childIndex = index + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = tree.GetState(childIndex);
            flags |= previousState.IsCompleted() ? previousState : VirtualMachine.Tick(childIndex, tree, bb);
            childIndex = tree.GetEndIndex(childIndex);
        }

        if (flags.HasFlagFast(NodeState.Running)) return NodeState.Running;
        if (flags.HasFlagFast(NodeState.Failure)) return NodeState.Failure;
        if (flags.HasFlagFast(NodeState.Success)) return NodeState.Success;
        return NodeState.Success;
    }
}

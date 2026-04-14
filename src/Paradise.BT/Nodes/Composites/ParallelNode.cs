namespace Paradise.BT;

[System.Runtime.InteropServices.Guid("A316D182-7D8C-4075-A46D-FEE08CAEEEAF")]
public struct ParallelNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        NodeState flags = 0;
        int endIndex = blob.GetEndIndex(index);
        int childIndex = index + 1;
        while (childIndex < endIndex)
        {
            NodeState previousState = blob.GetState(childIndex);
            flags |= previousState.IsCompleted() ? 0 : VirtualMachine.Tick(childIndex, ref blob, ref bb);
            childIndex = blob.GetEndIndex(childIndex);
        }

        if (flags.HasFlagFast(NodeState.Running)) return NodeState.Running;
        if (flags.HasFlagFast(NodeState.Failure)) return NodeState.Failure;
        if (flags.HasFlagFast(NodeState.Success)) return NodeState.Success;
        return 0;
    }
}

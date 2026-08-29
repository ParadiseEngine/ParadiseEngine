namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("8D789E4C-D4B8-41D9-A2CD-47C7024B1D51")]
[Builder(NodeCardinality.Decorator)]
public struct SucceederNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(blob, bb);
        return childState == NodeState.Failure ? NodeState.Success : childState;
    }
}

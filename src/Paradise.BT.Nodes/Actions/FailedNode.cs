namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("AC5CB763-5F7A-4301-9670-D4E38A5557CB")]
[Builder("Failure")]
public struct FailedNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Failure;
}

namespace Paradise.BT;

[System.Runtime.InteropServices.Guid("AC5CB763-5F7A-4301-9670-D4E38A5557CB")]
[Builder("Failure")]
public struct FailedNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => NodeState.Failure;
}

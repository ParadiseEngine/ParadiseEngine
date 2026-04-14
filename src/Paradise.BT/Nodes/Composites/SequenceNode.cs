namespace Paradise.BT;

[System.Runtime.InteropServices.Guid("8A3B18AE-C5E9-4F34-BCB7-BD645C5017A5")]
public struct SequenceNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => index.TickChildrenReturnLastOrDefault(ref blob, ref bb, static state => state.IsRunningOrFailure());
}

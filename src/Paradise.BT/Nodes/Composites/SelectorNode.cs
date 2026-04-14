namespace Paradise.BT;

[System.Runtime.InteropServices.Guid("BD4C1D8F-BA8E-4D74-9039-7D1E6010B058")]
public struct SelectorNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => index.TickChildrenReturnLastOrDefault(ref blob, ref bb, static state => state.IsRunningOrSuccess());
}

namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("BD4C1D8F-BA8E-4D74-9039-7D1E6010B058")]
[Builder(NodeCardinality.Composite)]
public struct SelectorNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => index.TickChildrenReturnLastOrDefault(blob, bb, static state => state.IsRunningOrSuccess());
}

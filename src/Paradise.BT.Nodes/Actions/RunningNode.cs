namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("F17339E0-D401-451B-864B-007AD44E05A3")]
[Builder]
public struct RunningNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => NodeState.Running;
}

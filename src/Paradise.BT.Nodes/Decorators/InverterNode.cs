namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("54CA6411-0DEA-4820-A8AF-7D7B76BC3875")]
[Builder(NodeCardinality.Decorator)]
public struct InverterNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        NodeState childState = index.TickChild(blob, bb);
        if (childState == NodeState.Success) return NodeState.Failure;
        if (childState == NodeState.Failure) return NodeState.Success;
        return childState;
    }
}

namespace Paradise.BT.Nodes;

[System.Runtime.InteropServices.Guid("54CA6411-0DEA-4820-A8AF-7D7B76BC3875")]
[Builder(NodeCardinality.Decorator)]
public struct InverterNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        NodeState childState = index.TickChild(ref blob, ref bb);
        if (childState == NodeState.Success) return NodeState.Failure;
        if (childState == NodeState.Failure) return NodeState.Success;
        return childState;
    }
}

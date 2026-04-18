using System.Runtime.InteropServices;

namespace Paradise.BT.Nodes;

[Guid("55BF6D71-62B0-4AB8-9B51-B0B58B7AC76A")]
public struct DelegateConditionNode : INodeData
{
    private readonly Func<IBlackboard, int, bool> _predicate;

    public DelegateConditionNode(Func<IBlackboard, int, bool> predicate)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => _predicate(bb, index).ToNodeState();
}

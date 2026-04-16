using System.Runtime.InteropServices;
using Paradise.BT;

namespace Paradise.BT.Sample;

[Guid("B0E631D3-64E8-467F-B39A-1E4AF41E6A66")]
public struct DelegateActionNode : INodeData
{
    private readonly Func<IBlackboard, int, NodeState> _action;

    public DelegateActionNode(Func<IBlackboard, int, NodeState> action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => _action(bb, index);
}

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

[Guid("2F6009D3-1314-42E6-8E52-4AEB7CDDB4CD")]
public struct DelayTimerNode : INodeData
{
    public float TimerSeconds;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        TimerSeconds -= bb.GetData<BehaviorTreeTickDeltaTime>().Value;
        return TimerSeconds <= 0f ? NodeState.Success : NodeState.Running;
    }
}

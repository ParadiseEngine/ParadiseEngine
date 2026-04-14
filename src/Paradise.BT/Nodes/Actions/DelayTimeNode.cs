namespace Paradise.BT;

/// <summary>
/// Pure .NET adaptation of EntitiesBT's delay timer action.
/// </summary>
[System.Runtime.InteropServices.Guid("2F6009D3-1314-42E6-8E52-4AEB7CDDB4CD")]
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

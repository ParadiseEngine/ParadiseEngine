using System.Runtime.InteropServices;

namespace Paradise.BT.Nodes;

/// <summary>
/// A single-shot timer that returns <see cref="NodeState.Running"/> until <see cref="TimerSeconds"/> counts down to zero.
/// </summary>
/// <remarks>
/// Field mutations on <see cref="TimerSeconds"/> across ticks are persistent because Paradise.BT stores each runtime
/// node as a boxed <c>RuntimeNode&lt;TNodeData&gt;</c> inside the blob. <c>Tick</c> receives a <c>ref</c> to the
/// boxed struct field, so <c>TimerSeconds -=</c> writes back into the live instance. On reset the VM repopulates
/// the runtime data from the default data via <c>CopyDefaultToRuntime()</c>, restarting the timer.
/// </remarks>
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

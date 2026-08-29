using System.Runtime.InteropServices;

namespace Paradise.BT.Nodes;

/// <summary>
/// A single-shot timer that returns <see cref="NodeState.Running"/> until <see cref="TimerSeconds"/> counts down to zero.
/// </summary>
/// <remarks>
/// Writes to <see cref="TimerSeconds"/> persist across ticks because <c>Tick</c> receives a
/// <c>ref</c> to the node's own bytes in the blob, so <c>TimerSeconds -=</c> lands in the live
/// instance. On reset the VM restores the runtime data from the authored default, restarting the
/// timer.
///
/// The only built-in that reads a blackboard. It declares nothing: the generator scans this body
/// and publishes the access as assembly metadata (<c>[assembly: NodeAccess]</c>), which is how a
/// consuming assembly's binding learns about the delta time with no hand-written declaration.
/// </remarks>
[Guid("2F6009D3-1314-42E6-8E52-4AEB7CDDB4CD")]
[Builder("Delay")]
public struct DelayTimerNode(float timerSeconds) : INodeData
{
    public float TimerSeconds = timerSeconds;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        TimerSeconds -= bb.GetData<BehaviorTreeTickDeltaTime>().Value;
        return TimerSeconds <= 0f ? NodeState.Success : NodeState.Running;
    }
}

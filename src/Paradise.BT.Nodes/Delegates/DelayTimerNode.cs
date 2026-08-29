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
/// The only built-in that reads a blackboard, which is why it is the only one declaring access.
/// Its generated builder (<c>Delay</c>) carries this type on its base, so a tree using it binds
/// the delta time without being told — provided the builder comes from a referenced assembly, as
/// it does here.
/// </remarks>
[Guid("2F6009D3-1314-42E6-8E52-4AEB7CDDB4CD")]
[Reads<BehaviorTreeTickDeltaTime>]
[Builder("Delay")]
public struct DelayTimerNode : INodeData
{
    public float TimerSeconds;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        TimerSeconds -= bb.GetData<BehaviorTreeTickDeltaTime>().Value;
        return TimerSeconds <= 0f ? NodeState.Success : NodeState.Running;
    }
}

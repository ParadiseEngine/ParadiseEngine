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
/// It is also built by a factory (<c>BuiltInBehaviorNodes.Delay</c>) rather than named, so a tree
/// using it has to list it in <c>[BehaviorTreeBinding(..., Also = [typeof(DelayTimerNode)])]</c>
/// for its delta time to be bound.
/// </remarks>
[Guid("2F6009D3-1314-42E6-8E52-4AEB7CDDB4CD")]
[Reads<BehaviorTreeTickDeltaTime>]
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

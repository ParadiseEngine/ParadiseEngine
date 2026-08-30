namespace Paradise.BT;

/// <summary>
/// One agent's mutable half of a compiled tree — a <see cref="NodeState"/> per node and each
/// node's live data as bytes, laid out by the shared <see cref="BehaviorTreeLayout"/> — with the
/// blackboard passed per call rather than stored, which is what admits a <c>ref struct</c>
/// (generated) blackboard. The buffers are ordinary managed arrays and need no pinning:
/// <see cref="IBehaviorTree"/> reaches node data by <c>ref</c>, not by address.
/// </summary>
/// <remarks>
/// Construction copies each node's authored defaults but does not run custom
/// <see cref="INode.Reset"/> hooks — those need a blackboard. Call <see cref="Reset"/> first
/// if any node type has one.
/// </remarks>
public class BehaviorTreeInstance
{
    private readonly BehaviorTreeLayout _layout;
    private readonly NodeState[] _states;
    private readonly byte[] _data;

    internal BehaviorTreeInstance(BehaviorTreeLayout layout)
    {
        _layout = layout;
        _states = new NodeState[layout.Blob.Count];
        _data = new byte[Math.Max(1, layout.Blob.DataSize)];
        AutoResetOnCompletion = true;
        BehaviorTreeRef.Initialize(_layout, _states, _data);
    }

    /// <summary>Restart a finished tree on its next tick, so an instance loops by default.</summary>
    public bool AutoResetOnCompletion { get; set; }

    public NodeState Status => _states[0];

    /// <summary>The blob over this instance's arrays. Built per use: a ref struct cannot be a
    /// field.</summary>
    private BehaviorTreeRef Blob => new(ref _layout.Blob, _states, _data);

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        if (AutoResetOnCompletion && Status.IsCompleted())
        {
            VirtualMachine.Reset(Blob, blackboard);
        }

        NodeState state = VirtualMachine.Tick(Blob, blackboard);
        return state;
    }

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        VirtualMachine.Reset(Blob, blackboard);
    }
}

namespace Paradise.BT;

/// <summary>
/// One agent's mutable half of a compiled tree — a <see cref="NodeState"/> per node and each
/// node's live data as bytes, laid out by the shared <see cref="BehaviorTreeLayout"/> — with the
/// blackboard passed per call rather than stored, which is what admits a <c>ref struct</c>
/// (generated) blackboard. The buffers are ordinary managed arrays and need no pinning:
/// <see cref="INodeBlob"/> reaches node data by <c>ref</c>, not by address.
/// </summary>
/// <remarks>
/// Construction copies each node's authored defaults but does not run custom
/// <see cref="INodeData.Reset"/> hooks — those need a blackboard. Call <see cref="Reset"/> first
/// if any node type has one.
/// </remarks>
public class BehaviorTreeInstance
{
    private readonly BehaviorTreeLayoutHandle _layout;
    private readonly NodeState[] _states;
    private readonly byte[] _data;

    /// <summary>Borrows <paramref name="layout"/>: whoever built the layout must keep it alive
    /// for as long as this instance ticks.</summary>
    public BehaviorTreeInstance(BehaviorTreeLayoutHandle layout)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException(
                "Behavior tree layout handle is not valid. Build or deserialize a "
                + $"{nameof(BehaviorTreeLayout)} and pass its handle.", nameof(layout));
        }

        _layout = layout;
        _states = new NodeState[layout.NodeCount];
        _data = new byte[Math.Max(1, layout.RuntimeDataSize)];
        AutoResetOnCompletion = true;

        UnmanagedNodeBlob.Initialize(_layout, _states, _data);
    }

    /// <summary>Restart a finished tree on its next tick, so an instance loops by default.</summary>
    public bool AutoResetOnCompletion { get; set; }

    public NodeState Status => _states[0];

    /// <summary>The blob over this instance's arrays. Built per use: a ref struct cannot be a
    /// field.</summary>
    private UnmanagedNodeBlob Blob => new(_layout, _states, _data);

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        if (AutoResetOnCompletion && Status.IsCompleted())
        {
            Reset(blackboard);
        }

        return VirtualMachine.Tick(Blob, blackboard);
    }

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
        => VirtualMachine.Reset(Blob, blackboard);
}

/// <summary>
/// <see cref="BehaviorTreeInstance"/> plus an owned blackboard, for an ordinary struct blackboard
/// that can live in a field. A generated (ref struct) blackboard cannot — bind one per tick and
/// use the base class directly.
/// </summary>
/// <remarks>
/// <see cref="Tick"/> performs no blackboard writes of its own. Per-tick inputs (e.g.
/// <see cref="BehaviorTreeTickDeltaTime"/> for time-based nodes) are the caller's responsibility:
/// write them via <see cref="Blackboard"/> before calling <see cref="Tick"/>.
/// </remarks>
public class BehaviorTreeInstance<TBlackboard> : BehaviorTreeInstance
    where TBlackboard : struct, IBlackboard
{
    private TBlackboard _blackboard;

    internal BehaviorTreeInstance(BehaviorTree tree, TBlackboard blackboard)
        : base(GetLayoutHandle(tree))
    {
        _blackboard = blackboard;
        Reset();
    }

    public ref TBlackboard Blackboard => ref _blackboard;

    public NodeState Tick() => Tick(_blackboard);

    public void Reset() => Reset(_blackboard);

    private static BehaviorTreeLayoutHandle GetLayoutHandle(BehaviorTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Layout.Handle;
    }
}

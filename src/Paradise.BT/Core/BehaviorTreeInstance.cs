namespace Paradise.BT;

/// <summary>
/// Mutable runtime state for a compiled <see cref="BehaviorTree"/>.
/// Parameterised over <typeparamref name="TBlackboard"/> so the tick pipeline stays allocation-free — the
/// <c>struct</c> + <see cref="IBlackboard"/> constraint matches <see cref="VirtualMachine"/>'s generic
/// signatures and lets the JIT specialise per concrete blackboard type.
///
/// <b>The state is two plain arrays.</b> What each node returned last tick, and each node's live
/// data as bytes, laid out by the tree's shared <see cref="BehaviorTreeLayout"/>. They are ordinary
/// managed arrays and need no pinning: <see cref="INodeBlob"/> reaches node data by <c>ref</c>, not
/// by address. The blob itself is a ref struct, so it is built per call rather than held.
/// </summary>
/// <remarks>
/// <see cref="Tick"/> performs no blackboard writes of its own. Per-tick inputs (e.g.
/// <see cref="BehaviorTreeTickDeltaTime"/> for time-based nodes) are the caller's responsibility: write them
/// via <see cref="Blackboard"/> before calling <see cref="Tick"/>.
/// </remarks>
public class BehaviorTreeInstance<TBlackboard>
    where TBlackboard : struct, IBlackboard
{
    private readonly BehaviorTreeLayoutHandle _layout;
    private readonly NodeState[] _states;
    private readonly byte[] _data;
    private TBlackboard _blackboard;

    internal BehaviorTreeInstance(BehaviorTree tree, TBlackboard blackboard)
    {
        ArgumentNullException.ThrowIfNull(tree);

        BehaviorTreeLayout layout = tree.Layout;
        _layout = layout.Handle;
        _states = new NodeState[layout.NodeCount];
        _data = new byte[Math.Max(1, layout.RuntimeDataSize)];
        _blackboard = blackboard;
        AutoResetOnCompletion = true;

        UnmanagedNodeBlob.Initialize(_layout, _states, _data);
        Reset();
    }

    public bool AutoResetOnCompletion { get; set; }

    public ref TBlackboard Blackboard => ref _blackboard;

    public NodeState Status => _states[0];

    /// <summary>The blob over this instance's arrays. Built per use: a ref struct cannot be a
    /// field.</summary>
    private UnmanagedNodeBlob Blob => new(_layout, _states, _data);

    public NodeState Tick()
    {
        if (AutoResetOnCompletion && Status.IsCompleted())
        {
            Reset();
        }

        UnmanagedNodeBlob blob = Blob;
        return VirtualMachine.Tick(blob, _blackboard);
    }

    public void Reset()
    {
        UnmanagedNodeBlob blob = Blob;
        VirtualMachine.Reset(blob, _blackboard);
    }
}

namespace Paradise.BT;

/// <summary>
/// Mutable runtime state for a compiled <see cref="BehaviorTree"/>.
/// Parameterised over <typeparamref name="TBlackboard"/> so the tick pipeline stays allocation-free — the
/// <c>struct</c> + <see cref="IBlackboard"/> constraint matches <see cref="VirtualMachine"/>'s generic signatures
/// and lets the JIT specialise per concrete blackboard type.
/// </summary>
public sealed class BehaviorTreeInstance<TBlackboard>
    where TBlackboard : struct, IBlackboard
{
    private readonly BehaviorTree _tree;
    private NodeBlob _blob;
    private TBlackboard _blackboard;

    internal BehaviorTreeInstance(BehaviorTree tree, TBlackboard blackboard)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _blackboard = blackboard;
        _blob = NodeBlob.Create(tree);
        AutoResetOnCompletion = true;
        Reset();
    }

    public bool AutoResetOnCompletion { get; set; }

    public ref TBlackboard Blackboard => ref _blackboard;

    public NodeState Status => _blob.GetState(0);

    public NodeState Tick(float deltaTime = 0f)
    {
        if (AutoResetOnCompletion && Status.IsCompleted())
        {
            Reset();
        }

        _blackboard.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
        return VirtualMachine.Tick(ref _blob, ref _blackboard);
    }

    public void Reset() => VirtualMachine.Reset(ref _blob, ref _blackboard);
}

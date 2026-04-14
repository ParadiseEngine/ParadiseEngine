namespace Paradise.BT;

/// <summary>
/// Mutable runtime state for a compiled <see cref="BehaviorTree"/>.
/// </summary>
public sealed class BehaviorTreeInstance
{
    private readonly BehaviorTree _tree;
    private NodeBlob _blob;
    private Blackboard _blackboard;

    internal BehaviorTreeInstance(BehaviorTree tree, Blackboard blackboard)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _blackboard = blackboard;
        _blob = NodeBlob.Create(tree);
        AutoResetOnCompletion = true;
        Reset();
    }

    public bool AutoResetOnCompletion { get; set; }

    public Blackboard Blackboard => _blackboard;

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

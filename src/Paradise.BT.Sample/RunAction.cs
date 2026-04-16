using Paradise.BT;
using Paradise.BT.Sample;
using Paradise.BT.Builder;

public sealed class RunAction : BTreeNode
{
    private readonly Func<IBlackboard, int, NodeState> _action;

    public RunAction(Func<IBlackboard, NodeState> action) : this((bb, _) => action(bb)) { }

    public RunAction(Func<IBlackboard, int, NodeState> action) => _action = action;

    protected override BehaviorNodeDefinition ToDefinition()
        => BehaviorNodes.Node(new DelegateActionNode(_action));
}

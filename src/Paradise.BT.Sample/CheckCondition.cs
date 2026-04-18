using Paradise.BT;
using Paradise.BT.Builder;
using Paradise.BT.Nodes;

public sealed class CheckCondition : BTreeNode
{
    private readonly Func<IBlackboard, int, bool> _predicate;

    public CheckCondition(Func<IBlackboard, bool> predicate) : this((bb, _) => predicate(bb)) { }

    public CheckCondition(Func<IBlackboard, int, bool> predicate) => _predicate = predicate;

    protected override BehaviorNodeDefinition ToDefinition()
        => BehaviorNodes.Node(new DelegateConditionNode(_predicate));
}

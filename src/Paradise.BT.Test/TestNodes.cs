namespace Paradise.BT.Test;

public static class TestBehaviorNodes
{
    public static BehaviorNodeDefinition Action(Func<IBlackboard, NodeState> action)
        => Action((bb, _) => action(bb));

    public static BehaviorNodeDefinition Action(Func<IBlackboard, int, NodeState> action)
        => BehaviorNodes.Node(new DelegateActionNode(action));

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, bool> predicate)
        => Condition((bb, _) => predicate(bb));

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, int, bool> predicate)
        => BehaviorNodes.Node(new DelegateConditionNode(predicate));

    public static BehaviorTreeSerializationRegistry BuiltInRegistry()
        => BuiltInBehaviorNodes.CreateRegistry();
}

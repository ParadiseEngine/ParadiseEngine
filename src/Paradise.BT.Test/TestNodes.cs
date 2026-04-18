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

internal static class TestTickExtensions
{
    /// <summary>
    /// Test-only convenience: writes <see cref="BehaviorTreeTickDeltaTime"/> to the blackboard then ticks.
    /// The library intentionally does not expose this — delta-time propagation is a caller concern.
    /// </summary>
    public static NodeState Tick<TBlackboard>(this BehaviorTreeInstance<TBlackboard> instance, float deltaTime)
        where TBlackboard : struct, IMutableBlackboard
    {
        instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
        return instance.Tick();
    }
}

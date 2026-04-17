namespace Paradise.BT.Test;

public static class TestBehaviorNodes
{
    public static BehaviorNodeDefinition Success()
        => BehaviorNodes.Node(new SuccessNode());

    public static BehaviorNodeDefinition Failure()
        => BehaviorNodes.Node(new FailedNode());

    public static BehaviorNodeDefinition Running()
        => BehaviorNodes.Node(new RunningNode());

    public static BehaviorNodeDefinition Sequence(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new SequenceNode(), children);

    public static BehaviorNodeDefinition Selector(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new SelectorNode(), children);

    public static BehaviorNodeDefinition Parallel(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new ParallelNode(), children);

    public static BehaviorNodeDefinition Inverter(BehaviorNodeDefinition child)
        => BehaviorNodes.Node(new InverterNode(), child);

    public static BehaviorNodeDefinition Succeeder(BehaviorNodeDefinition child)
        => BehaviorNodes.Node(new SucceederNode(), child);

    public static BehaviorNodeDefinition Repeat(int count, BehaviorNodeDefinition child, NodeState breakStates = 0)
        => BehaviorNodes.Node(new RepeatTimesNode { TickTimes = count, BreakStates = breakStates }, child);

    public static BehaviorNodeDefinition RepeatForever(BehaviorNodeDefinition child, NodeState breakStates = 0)
        => BehaviorNodes.Node(new RepeatForeverNode { BreakStates = breakStates }, child);

    public static BehaviorNodeDefinition Delay(float seconds)
        => BehaviorNodes.Node(new DelayTimerNode { TimerSeconds = seconds });

    public static BehaviorNodeDefinition Action(Func<IBlackboard, NodeState> action)
        => Action((bb, _) => action(bb));

    public static BehaviorNodeDefinition Action(Func<IBlackboard, int, NodeState> action)
        => BehaviorNodes.Node(new DelegateActionNode(action));

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, bool> predicate)
        => Condition((bb, _) => predicate(bb));

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, int, bool> predicate)
        => BehaviorNodes.Node(new DelegateConditionNode(predicate));

    public static BehaviorTreeSerializationRegistry BuiltInRegistry()
        => new BehaviorTreeSerializationRegistry()
            .Register<SequenceNode>()
            .Register<SelectorNode>()
            .Register<ParallelNode>()
            .Register<InverterNode>()
            .Register<SucceederNode>()
            .Register<RepeatTimesNode>()
            .Register<RepeatForeverNode>()
            .Register<SuccessNode>()
            .Register<FailedNode>()
            .Register<RunningNode>()
            .Register<DelayTimerNode>();
}

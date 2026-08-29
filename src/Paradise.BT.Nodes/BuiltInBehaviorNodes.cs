namespace Paradise.BT.Nodes;

/// <summary>
/// Convenience factory helpers for composing trees from the built-in node types shipped in Paradise.BT.Nodes,
/// and a preconfigured <see cref="BehaviorTreeSerializationRegistry"/> for deserialization.
/// </summary>
public static class BuiltInBehaviorNodes
{
    [Builds<SuccessNode>]
    public static BehaviorNodeDefinition Success()
        => BehaviorNodes.Node(new SuccessNode());

    [Builds<FailedNode>]
    public static BehaviorNodeDefinition Failure()
        => BehaviorNodes.Node(new FailedNode());

    [Builds<RunningNode>]
    public static BehaviorNodeDefinition Running()
        => BehaviorNodes.Node(new RunningNode());

    [Builds<SequenceNode>]
    public static BehaviorNodeDefinition Sequence(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new SequenceNode(), children);

    [Builds<SelectorNode>]
    public static BehaviorNodeDefinition Selector(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new SelectorNode(), children);

    [Builds<ParallelNode>]
    public static BehaviorNodeDefinition Parallel(params BehaviorNodeDefinition[] children)
        => BehaviorNodes.Node(new ParallelNode(), children);

    [Builds<InverterNode>]
    public static BehaviorNodeDefinition Inverter(BehaviorNodeDefinition child)
        => BehaviorNodes.Node(new InverterNode(), child);

    [Builds<SucceederNode>]
    public static BehaviorNodeDefinition Succeeder(BehaviorNodeDefinition child)
        => BehaviorNodes.Node(new SucceederNode(), child);

    [Builds<RepeatTimesNode>]
    public static BehaviorNodeDefinition Repeat(int count, BehaviorNodeDefinition child, NodeState breakStates = 0)
        => BehaviorNodes.Node(new RepeatTimesNode { TickTimes = count, BreakStates = breakStates }, child);

    [Builds<RepeatForeverNode>]
    public static BehaviorNodeDefinition RepeatForever(BehaviorNodeDefinition child, NodeState breakStates = 0)
        => BehaviorNodes.Node(new RepeatForeverNode { BreakStates = breakStates }, child);

    [Builds<DelayTimerNode>]
    public static BehaviorNodeDefinition Delay(float seconds)
        => BehaviorNodes.Node(new DelayTimerNode { TimerSeconds = seconds });

    /// <summary>
    /// Creates a <see cref="BehaviorTreeSerializationRegistry"/> pre-populated with every node type
    /// shipped in Paradise.BT.Nodes. Register additional custom types on the returned registry via
    /// <see cref="BehaviorTreeSerializationRegistry.Register{T}"/>.
    /// </summary>
    // There is deliberately no RegisterAll() here. Every node type in this assembly registers
    // itself with NodeTypeRegistry through a generated module initializer (Paradise.BT.Generators),
    // so a node added here needs no second edit and cannot be forgotten. CreateRegistry below is a
    // different thing and stays: DESERIALIZATION takes a registry instance the caller passes.

    public static BehaviorTreeSerializationRegistry CreateRegistry()
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

namespace Paradise.BT;

/// <summary>
/// Convenience factory helpers layered on top of the exact EntitiesBT-style node interfaces.
/// </summary>
public static class BehaviorNodes
{
    public static BehaviorNodeDefinition Node<TNodeData>(TNodeData nodeData, params BehaviorNodeDefinition[] children)
        where TNodeData : struct, INodeData
        => Node(nodeData, InferNodeType(children.Length), children);

    public static BehaviorNodeDefinition Node<TNodeData>(TNodeData nodeData, BehaviorNodeType nodeType, params BehaviorNodeDefinition[] children)
        where TNodeData : struct, INodeData
        => BehaviorNodeDefinition.Create(nodeData, BehaviorNodeMetadata<TNodeData>.Get(nodeType), children);

    public static BehaviorNodeDefinition Sequence(params BehaviorNodeDefinition[] children)
        => Node(new SequenceNode(), BehaviorNodeType.Composite, children);

    public static BehaviorNodeDefinition Selector(params BehaviorNodeDefinition[] children)
        => Node(new SelectorNode(), BehaviorNodeType.Composite, children);

    public static BehaviorNodeDefinition Parallel(params BehaviorNodeDefinition[] children)
        => Node(new ParallelNode(), BehaviorNodeType.Composite, children);

    public static BehaviorNodeDefinition Repeat(int count, BehaviorNodeDefinition child, NodeState breakStates = 0)
        => Node(new RepeatTimesNode { TickTimes = count, BreakStates = breakStates }, BehaviorNodeType.Decorate, child);

    public static BehaviorNodeDefinition RepeatForever(BehaviorNodeDefinition child, NodeState breakStates = 0)
        => Node(new RepeatForeverNode { BreakStates = breakStates }, BehaviorNodeType.Decorate, child);

    public static BehaviorNodeDefinition Inverter(BehaviorNodeDefinition child)
        => Node(new InverterNode(), BehaviorNodeType.Decorate, child);

    public static BehaviorNodeDefinition Succeeder(BehaviorNodeDefinition child)
        => Node(new SucceederNode(), BehaviorNodeType.Decorate, child);

    public static BehaviorNodeDefinition Delay(float seconds)
        => Node(new DelayTimerNode { TimerSeconds = seconds }, BehaviorNodeType.Action);

    public static BehaviorNodeDefinition Action(Func<IBlackboard, NodeState> action)
        => Action((bb, _) => action(bb));

    public static BehaviorNodeDefinition Action(Func<IBlackboard, int, NodeState> action)
        => Node(new DelegateActionNode(action), BehaviorNodeType.Action);

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, bool> predicate)
        => Condition((bb, _) => predicate(bb));

    public static BehaviorNodeDefinition Condition(Func<IBlackboard, int, bool> predicate)
        => Node(new DelegateConditionNode(predicate), BehaviorNodeType.Action);

    public static BehaviorNodeDefinition Success() => Node(new SuccessNode(), BehaviorNodeType.Action);

    public static BehaviorNodeDefinition Failure() => Node(new FailedNode(), BehaviorNodeType.Action);

    public static BehaviorNodeDefinition Running() => Node(new RunningNode(), BehaviorNodeType.Action);

    private static BehaviorNodeType InferNodeType(int childCount)
        => childCount switch
        {
            <= 0 => BehaviorNodeType.Action,
            1 => BehaviorNodeType.Decorate,
            _ => BehaviorNodeType.Composite,
        };

    private static class BehaviorNodeMetadata<TNodeData>
        where TNodeData : struct, INodeData
    {
        public static readonly Guid Guid = typeof(TNodeData).GetNodeGuid();

        public static BehaviorNodeMetadata Get(BehaviorNodeType nodeType) => new BehaviorNodeMetadata(Guid, nodeType);
    }
}

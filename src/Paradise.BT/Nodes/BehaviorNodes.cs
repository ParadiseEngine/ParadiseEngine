namespace Paradise.BT;

/// <summary>
/// Convenience factory helpers layered on top of the exact EntitiesBT-style node interfaces.
/// </summary>
public static class BehaviorNodes
{
    public static BehaviorNodeDefinition Node<TNodeData>(TNodeData nodeData, params BehaviorNodeDefinition[] children)
        where TNodeData : struct, INodeData
        => BehaviorNodeDefinition.Create(nodeData, BehaviorNodeMetadata<TNodeData>.Metadata, children);

    public static BehaviorNodeDefinition Sequence(params BehaviorNodeDefinition[] children)
        => Node(new SequenceNode(), children);

    public static BehaviorNodeDefinition Selector(params BehaviorNodeDefinition[] children)
        => Node(new SelectorNode(), children);

    public static BehaviorNodeDefinition Parallel(params BehaviorNodeDefinition[] children)
        => Node(new ParallelNode(), children);

    public static BehaviorNodeDefinition Repeat(int count, BehaviorNodeDefinition child, NodeState breakStates = 0)
        => Node(new RepeatTimesNode { TickTimes = count, BreakStates = breakStates }, child);

    public static BehaviorNodeDefinition RepeatForever(BehaviorNodeDefinition child, NodeState breakStates = 0)
        => Node(new RepeatForeverNode { BreakStates = breakStates }, child);

    public static BehaviorNodeDefinition Inverter(BehaviorNodeDefinition child)
        => Node(new InverterNode(), child);

    public static BehaviorNodeDefinition Succeeder(BehaviorNodeDefinition child)
        => Node(new SucceederNode(), child);

    public static BehaviorNodeDefinition Success() => Node(new SuccessNode());

    public static BehaviorNodeDefinition Failure() => Node(new FailedNode());

    public static BehaviorNodeDefinition Running() => Node(new RunningNode());

    private static class BehaviorNodeMetadata<TNodeData>
        where TNodeData : struct, INodeData
    {
        public static readonly BehaviorNodeMetadata Metadata = new(typeof(TNodeData).GetNodeGuid());
    }
}

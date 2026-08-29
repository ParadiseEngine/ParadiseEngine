namespace Paradise.BT.Nodes;

/// <summary>
/// A <see cref="BehaviorTreeSerializationRegistry"/> preloaded with every node type shipped here.
/// Register custom types on the result.
///
/// Deserialization is the one case that needs a list: node REGISTRATION is generated, so a node
/// added here needs no second edit.
/// </summary>
public static class BuiltInNodeRegistry
{
    public static BehaviorTreeSerializationRegistry Create()
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

namespace Paradise.BT.Nodes;

/// <summary>
/// A <see cref="BehaviorTreeSerializationRegistry"/> preloaded with every node type shipped in
/// Paradise.BT.Nodes. Register custom types on the returned registry with
/// <see cref="BehaviorTreeSerializationRegistry.Register{T}"/>.
///
/// This is all that survives of the old <c>BuiltInBehaviorNodes</c>. Its factory methods are gone:
/// each built-in node has a generated builder, so <c>new Sequence(a, b)</c> replaced
/// <c>BuiltInBehaviorNodes.Sequence(a, b)</c>. A factory method was the one way of composing a tree
/// that DISCARDED the node type — its return type is a definition, which says nothing about what
/// built it — so a binding could not see through it without being told.
///
/// There is deliberately no RegisterAll() here either. Every node type in this assembly registers
/// itself with NodeTypeRegistry through a generated module initializer, so a node added here needs
/// no second edit and cannot be forgotten. Deserialization is the different case that stays: it
/// takes a registry instance the caller passes.
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

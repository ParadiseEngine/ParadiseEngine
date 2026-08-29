namespace Paradise.BT.Builder;

/// <summary>
/// The type that builds one tree. Implementing this is what the binding generator keys on: it
/// sweeps the implementing type for the node types it names, unions their access, and emits a
/// <c>{Type}Blackboard</c> plus its <c>Bind</c>.
///
/// An interface rather than an attribute, for two things an attribute cannot give: the shape is
/// compile-checked — a tree type MUST have a <c>Build</c> returning its root — and a tree can be
/// a type parameter (<c>where TTree : IBehaviorTreeBuilder</c>), so infrastructure can build and
/// compile trees generically.
/// </summary>
public interface IBehaviorTreeBuilder
{
    static abstract BTreeNode Build();
}

/// <summary>
/// A tree whose <c>Build</c> takes arguments — tuning read at world build, a config. Same
/// generator behavior as <see cref="IBehaviorTreeBuilder"/>.
/// </summary>
public interface IBehaviorTreeBuilder<TArgs>
{
    static abstract BTreeNode Build(TArgs args);
}

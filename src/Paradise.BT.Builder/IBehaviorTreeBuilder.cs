namespace Paradise.BT.Builder;

/// <summary>
/// The type that builds one tree — what the binding generator keys on: it sweeps the implementing
/// type for the nodes it composes, unions their access, and emits <c>{Type}Blackboard</c> plus
/// its <c>Bind</c>. An interface rather than an attribute so the shape is compile-checked and a
/// tree can be a type parameter.
/// </summary>
public interface IBehaviorTreeBuilder
{
    static abstract BTreeNode Build();
}

/// <summary>A tree whose <c>Build</c> takes arguments — tuning read at world build.</summary>
public interface IBehaviorTreeBuilder<TArgs>
{
    static abstract BTreeNode Build(TArgs args);
}

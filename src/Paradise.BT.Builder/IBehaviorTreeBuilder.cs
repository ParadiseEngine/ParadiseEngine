namespace Paradise.BT.Builder;

/// <summary>Builds a tree whose node access determines its generated blackboard and Bind method.</summary>
/// <remarks>The static interface supports tree type parameters and compile-time shape checks.</remarks>
public interface IBehaviorTreeBuilder
{
    static abstract BTreeNode Build();
}

/// <summary>A tree whose <c>Build</c> takes arguments — tuning read at world build.</summary>
public interface IBehaviorTreeBuilder<TArgs>
{
    static abstract BTreeNode Build(TArgs args);
}

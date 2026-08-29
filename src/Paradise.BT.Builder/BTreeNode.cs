namespace Paradise.BT.Builder;

public abstract class BTreeNode
{
    protected internal abstract BehaviorNodeDefinition ToDefinition();

    public BehaviorTree Build()
        => BehaviorTreeBuilder.Build(ToDefinition());

    /// <summary>
    /// A builder IS a node definition, wherever one is expected.
    ///
    /// The two were parallel vocabularies for the same thing — a definition tree assembled by
    /// hand, and the generated builders over it — with no way to mix them, so a helper returning
    /// one could not be a child of the other. That is what kept a third way alive
    /// (<c>BuiltInBehaviorNodes</c>) purely to produce definitions, and a factory method is the one
    /// shape a binding scan cannot see through, since its return type says nothing about the node
    /// it built.
    /// </summary>
    public static implicit operator BehaviorNodeDefinition(BTreeNode node)
        => node is null ? throw new ArgumentNullException(nameof(node)) : node.ToDefinition();
}

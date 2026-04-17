namespace Paradise.BT.Builder;

public abstract class BTreeNode
{
    protected internal abstract BehaviorNodeDefinition ToDefinition();

    public BehaviorTree Build()
        => BehaviorTreeBuilder.Build(ToDefinition());
}

namespace Paradise.BT;

public enum NodeCardinality
{
    Leaf,
    Decorator,
    Composite
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class BuilderAttribute : Attribute
{
    public BuilderAttribute(NodeCardinality cardinality = NodeCardinality.Leaf)
        => Cardinality = cardinality;

    public BuilderAttribute(string name, NodeCardinality cardinality = NodeCardinality.Leaf)
    {
        Name = name;
        Cardinality = cardinality;
    }

    public string? Name { get; }
    public NodeCardinality Cardinality { get; }
}

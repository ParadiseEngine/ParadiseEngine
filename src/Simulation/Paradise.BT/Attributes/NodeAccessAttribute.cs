namespace Paradise.BT;

/// <summary>Publishes generated node access metadata for consumers without the node's source.</summary>
/// <remarks>Emitted by the BT generator; handwritten Reads/Writes attributes remain optional.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NodeAccessAttribute : Attribute
{
    public NodeAccessAttribute(Type node) => Node = node;

    public Type Node { get; }

    public Type[]? Reads { get; set; }

    public Type[]? Writes { get; set; }
}

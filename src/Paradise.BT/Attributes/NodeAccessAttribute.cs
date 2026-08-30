namespace Paradise.BT;

/// <summary>
/// Generated, per assembly, for every registrable node: what the node's <c>Tick</c> body touches,
/// published as metadata so a consuming assembly's binding can read access where no body exists.
/// This is what makes hand-written <see cref="ReadsAttribute{T}"/> / <see cref="WritesAttribute{T}"/>
/// optional for cross-assembly nodes rather than required. Written by the BT generator; not
/// intended to be written by hand.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NodeAccessAttribute : Attribute
{
    public NodeAccessAttribute(Type node) => Node = node;

    public Type Node { get; }

    public Type[]? Reads { get; set; }

    public Type[]? Writes { get; set; }
}

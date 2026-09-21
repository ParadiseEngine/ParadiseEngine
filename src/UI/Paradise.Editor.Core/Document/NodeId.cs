namespace Paradise.Editor.Core.Document;

/// <summary>Durable identity of an object in a scene: the Guid its <c>meta</c> component carries.</summary>
/// <remarks>A Guid rather than an index or a name, because the document may reorder or rename
/// and the selection, undo history and object references must all survive that.</remarks>
public readonly record struct NodeId(Guid Value)
{
    public static NodeId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

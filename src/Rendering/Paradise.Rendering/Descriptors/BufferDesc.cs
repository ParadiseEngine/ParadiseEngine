namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU buffer.</summary>
/// <remarks>Uploads use the renderer's create-with-data and update operations; this descriptor
/// does not expose CPU mapping flags because the public renderer has no map/unmap API.</remarks>
public readonly record struct BufferDesc(
    string? Name,
    ulong Size,
    BufferUsage Usage);

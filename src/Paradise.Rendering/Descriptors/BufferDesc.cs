namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU buffer.</summary>
public readonly record struct BufferDesc(
    string? Name,
    ulong Size,
    BufferUsage Usage,
    bool MappedAtCreation = false);

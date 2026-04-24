namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU buffer. M1 only exposes immutable-by-default buffers
/// initialised via <c>WebGpuRenderer.CreateBufferWithData</c> (backed by <c>Queue.WriteBuffer</c>).
/// A <c>MappedAtCreation</c>/map/unmap API is deferred to a later milestone — the flag is
/// deliberately absent until there is a corresponding public map/unmap path to pair with it.</summary>
public readonly record struct BufferDesc(
    string? Name,
    ulong Size,
    BufferUsage Usage);

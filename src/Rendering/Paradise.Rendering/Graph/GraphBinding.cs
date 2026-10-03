namespace Paradise.Rendering.Graph;

/// <summary>One bind-group entry whose kind determines its graph read or write dependency.</summary>
/// <remarks>Raw views, buffers and samplers add no dependency; declare any graph-tracked resource
/// access explicitly when using those bindings.</remarks>
public readonly record struct GraphBinding(uint Binding, GraphBindingKind Kind, GraphTexture Target, BindGroupEntryDesc Raw, GraphBuffer TargetBuffer = default)
{
    /// <summary>Sample an owned texture through its 2D view.</summary>
    public static GraphBinding Texture(uint binding, GraphTexture texture) =>
        new(binding, GraphBindingKind.TextureView, texture, default);

    /// <summary>Sample an owned texture through its 2D-array view.</summary>
    public static GraphBinding TextureArray(uint binding, GraphTexture texture) =>
        new(binding, GraphBindingKind.TextureArrayView, texture, default);

    /// <summary>Write an owned texture as a storage texture, through its 2D view. This is a WRITE
    /// of the texture: the pass becomes its producer, so a consumer's read keeps the pass alive
    /// and a pass that also reads the texture is refused.</summary>
    public static GraphBinding StorageTexture(uint binding, GraphTexture texture) =>
        new(binding, GraphBindingKind.StorageTextureView, texture, default);

    /// <summary>Bind a window of a buffer the graph tracks. <paramref name="write"/> makes the pass
    /// the buffer's producer; otherwise the pass depends on whoever wrote it. Named apart from the
    /// raw <see cref="Buffer"/> on purpose: a tracked buffer bound through the raw overload would
    /// silently lose its edge, and a missed write edge is the culling bug the graph exists to prevent.</summary>
    public static GraphBinding TrackedBuffer(uint binding, GraphBuffer buffer, ulong offset, ulong size, bool write = false) =>
        new(binding, write ? GraphBindingKind.BufferWrite : GraphBindingKind.BufferRead, GraphTexture.Invalid,
            BindGroupEntryDesc.ForBuffer(binding, default, offset, size), buffer);

    /// <summary>A texture view the graph does not own.</summary>
    public static GraphBinding View(uint binding, TextureViewHandle view) =>
        new(binding, GraphBindingKind.Raw, GraphTexture.Invalid, BindGroupEntryDesc.ForTextureView(binding, view));

    public static GraphBinding Sampler(uint binding, SamplerHandle sampler) =>
        new(binding, GraphBindingKind.Raw, GraphTexture.Invalid, BindGroupEntryDesc.ForSampler(binding, sampler));

    public static GraphBinding Buffer(uint binding, BufferHandle buffer, ulong offset, ulong size) =>
        new(binding, GraphBindingKind.Raw, GraphTexture.Invalid, BindGroupEntryDesc.ForBuffer(binding, buffer, offset, size));
}

public enum GraphBindingKind : byte
{
    Raw,
    TextureView,
    TextureArrayView,
    StorageTextureView,
    BufferRead,
    BufferWrite,
}

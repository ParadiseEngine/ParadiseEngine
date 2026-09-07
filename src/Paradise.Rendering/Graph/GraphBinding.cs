namespace Paradise.Rendering.Graph;

/// <summary>One entry of a bind group declared on a pass. Naming a <see cref="GraphTexture"/>
/// here is what makes the pass read it: the graph derives the dependency from the binding, so a
/// read that exists cannot go undeclared and a declaration that exists cannot be forgotten.
/// Resources the graph does not own — a LUT, a uniform buffer, a sampler — bind as they are.</summary>
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

    /// <summary>Bind a window of a tracked buffer. <paramref name="write"/> makes the pass the
    /// buffer's producer; otherwise the pass depends on whoever wrote it.</summary>
    public static GraphBinding Buffer(uint binding, GraphBuffer buffer, ulong offset, ulong size, bool write = false) =>
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

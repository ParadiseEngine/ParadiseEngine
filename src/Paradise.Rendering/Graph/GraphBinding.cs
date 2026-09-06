namespace Paradise.Rendering.Graph;

/// <summary>One entry of a bind group declared on a pass. Naming a <see cref="GraphTexture"/>
/// here is what makes the pass read it: the graph derives the dependency from the binding, so a
/// read that exists cannot go undeclared and a declaration that exists cannot be forgotten.
/// Resources the graph does not own — a LUT, a uniform buffer, a sampler — bind as they are.</summary>
public readonly record struct GraphBinding(uint Binding, GraphBindingKind Kind, GraphTexture Target, BindGroupEntryDesc Raw)
{
    /// <summary>Sample an owned texture through its 2D view.</summary>
    public static GraphBinding Texture(uint binding, GraphTexture texture) =>
        new(binding, GraphBindingKind.TextureView, texture, default);

    /// <summary>Sample an owned texture through its 2D-array view.</summary>
    public static GraphBinding TextureArray(uint binding, GraphTexture texture) =>
        new(binding, GraphBindingKind.TextureArrayView, texture, default);

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
}

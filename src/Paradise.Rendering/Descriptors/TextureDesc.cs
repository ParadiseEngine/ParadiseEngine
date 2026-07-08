namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU texture.</summary>
public readonly record struct TextureDesc(
    string? Name,
    uint Width,
    uint Height,
    uint DepthOrArrayLayers,
    uint MipLevelCount,
    uint SampleCount,
    TextureDimension Dimension,
    TextureFormat Format,
    TextureUsage Usage);

/// <summary>Creation parameters for a view into a texture: which array-layer range and how it is
/// interpreted. A single-layer <see cref="TextureViewDimension.D2"/> view is a render target for
/// one layer; a <see cref="TextureViewDimension.D2Array"/> view over all layers is what the shader
/// samples.</summary>
public readonly record struct TextureViewDesc(
    string? Name,
    TextureHandle Texture,
    TextureViewDimension Dimension,
    uint BaseArrayLayer,
    uint ArrayLayerCount);

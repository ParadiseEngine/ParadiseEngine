namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU texture view. <see cref="Format"/> may override the
/// parent texture's format for reinterpretation (e.g. view an Rgba8UnormSrgb texture as
/// Rgba8Unorm); pass <see cref="TextureFormat.Undefined"/> to inherit. <see cref="MipLevelCount"/>
/// and <see cref="ArrayLayerCount"/> of <c>0</c> cover "all remaining" starting at the base.</summary>
public readonly record struct RenderViewDesc(
    string? Name,
    TextureFormat Format,
    TextureViewDimension Dimension,
    TextureAspect Aspect,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount);

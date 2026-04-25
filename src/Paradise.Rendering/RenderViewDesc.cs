namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU texture view. <see cref="Format"/> must match the parent
/// texture's format in M2 — view-format reinterpretation (e.g. view an Rgba8UnormSrgb texture as
/// Rgba8Unorm) requires a <c>TextureDesc.ViewFormats</c> allowlist that has not yet landed and is
/// reserved for a later milestone. <see cref="MipLevelCount"/> and <see cref="ArrayLayerCount"/>
/// of <c>0</c> cover "all remaining" starting at the base.</summary>
public readonly record struct RenderViewDesc(
    string? Name,
    TextureFormat Format,
    TextureViewDimension Dimension,
    TextureAspect Aspect,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount);

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

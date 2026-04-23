namespace Paradise.Rendering;

/// <summary>Creation parameters for a GPU texture.</summary>
public readonly struct TextureDesc
{
    public readonly string? Name;
    public readonly uint Width;
    public readonly uint Height;
    public readonly uint DepthOrArrayLayers;
    public readonly uint MipLevelCount;
    public readonly uint SampleCount;
    public readonly TextureDimension Dimension;
    public readonly TextureFormat Format;
    public readonly TextureUsage Usage;

    public TextureDesc(
        string? name,
        uint width,
        uint height,
        uint depthOrArrayLayers,
        uint mipLevelCount,
        uint sampleCount,
        TextureDimension dimension,
        TextureFormat format,
        TextureUsage usage)
    {
        Name = name;
        Width = width;
        Height = height;
        DepthOrArrayLayers = depthOrArrayLayers;
        MipLevelCount = mipLevelCount;
        SampleCount = sampleCount;
        Dimension = dimension;
        Format = format;
        Usage = usage;
    }
}

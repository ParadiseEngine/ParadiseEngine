using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>The current frame's shadow atlas and admitted light views.</summary>
/// <remarks>View and lookup storage belongs to this frame and must not be retained across renders.</remarks>
public readonly record struct ShadowFrameData(
    GraphTexture Atlas,
    SamplerHandle Sampler,
    uint AtlasSize,
    float BlurTexels,
    float CascadeBlend,
    IReadOnlyList<ShadowView> Views,
    ReadOnlyMemory<int> FirstViews,
    ReadOnlyMemory<int> ViewCounts,
    ReadOnlyMemory<float> TexelWorlds)
{
    public static FrameDataKey<ShadowFrameData> Key { get; } = new("Pbr.Shadows");

    public int FirstView(int light) => (uint)light < (uint)FirstViews.Length ? FirstViews.Span[light] : -1;
    public int ViewCount(int light) => (uint)light < (uint)ViewCounts.Length ? ViewCounts.Span[light] : 0;
    public float TexelWorld(int light) => (uint)light < (uint)TexelWorlds.Length ? TexelWorlds.Span[light] : 0f;
}

/// <summary>The current frame's Forward+ froxel masks and projection range.</summary>
public readonly record struct LightGridFrameData(
    GraphBuffer Masks,
    ulong BufferBytes,
    int TilesX,
    int TilesY,
    int ZSlices,
    float Near,
    float Far)
{
    public static FrameDataKey<LightGridFrameData> Key { get; } = new("Pbr.LightGrid");
}

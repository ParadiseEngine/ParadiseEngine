namespace Paradise.Rendering.Pbr;

/// <summary>The names the PBR features declare their targets under, so a feature added by a game
/// can ask the graph for the HDR scene or the depth buffer by name rather than by reaching into
/// the renderer.</summary>
public static class PbrTargets
{
    public const TextureFormat HdrFormat = TextureFormat.Rgba16Float;

    /// <summary>Linear HDR scene color, the main pass's output and every post pass's input.</summary>
    public const string Hdr = "PbrHdrScene";

    /// <summary>The scene depth buffer.</summary>
    public const string Depth = "PbrDepth";

    /// <summary>The opaque scene captured before transparent geometry draws; exported, because a
    /// game's blend material samples it. Exists only while capture is on.</summary>
    public const string SceneColor = "PbrSceneColor";

    /// <summary>World positions from the SSAO pre-pass.</summary>
    public const string SsaoPosition = "PbrSsaoPosition";

    /// <summary>The pre-pass's own depth; written and never read.</summary>
    public const string SsaoPrepassDepth = "PbrSsaoPrepassDepth";

    /// <summary>The shadow-map array, one layer per shadow view.</summary>
    public const string ShadowArray = "PbrShadowArray";

    internal static readonly string[] Bloom = ["PbrBloom0", "PbrBloom1", "PbrBloom2", "PbrBloom3", "PbrBloom4", "PbrBloom5"];

    internal static TextureDesc RenderTarget(uint width, uint height, TextureFormat format, uint layers = 1) => new(
        null, width, height, layers, 1, 1, TextureDimension.D2, format,
        TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
}

/// <summary>Names under which the PBR features publish a frame's results on the
/// <see cref="Graph.FrameBlackboard"/>. A name absent from the blackboard means the feature that
/// produces it did not run this frame; consumers bind the black fallback instead.</summary>
public static class PbrResults
{
    /// <summary>Bloom mip 0, published only in frames the chain runs.</summary>
    public const string Bloom = "Pbr.Bloom";

    /// <summary>The SSAO position target, published only in frames the pre-pass runs.</summary>
    public const string SsaoPosition = "Pbr.Ssao.Position";
}

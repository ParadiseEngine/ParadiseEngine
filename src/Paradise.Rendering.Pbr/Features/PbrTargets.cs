namespace Paradise.Rendering.Pbr;

/// <summary>The names the PBR features declare their targets under, so a feature added by a game
/// can ask the graph for the HDR scene or the depth buffer by name rather than by reaching into
/// the renderer.</summary>
public static class PbrTargets
{
    public const string FogColor = "PbrFogColor";
    public const TextureFormat HdrFormat = TextureFormat.Rgba16Float;

    /// <summary>Linear HDR scene color, the main pass's output and every post pass's input.</summary>
    public const string Hdr = "PbrHdrScene";

    /// <summary>Linear tonemapped color before display effects and the output transfer function.</summary>
    public const string DisplayColor = "PbrDisplayColor";

    /// <summary>The scene depth buffer.</summary>
    public const string Depth = "PbrDepth";

    public const string OccluderDepth = "PbrOccluderDepth";

    /// <summary>The opaque scene captured before transparent geometry draws; exported, because a
    /// game's blend material samples it. Exists only while capture is on.</summary>
    public const string SceneColor = "PbrSceneColor";

    /// <summary>World-space normals of the opaque scene from the depth + normal pre-pass
    /// (Rgba16Float: xyz unit normal, w coverage).</summary>
    public const string PrepassNormal = "PbrPrepassNormal";

    /// <summary>The pre-pass's depth: the opaque scene's Depth32Float before the main pass, read
    /// as an unfilterable float by screen-space effects.</summary>
    public const string PrepassDepth = "PbrPrepassDepth";

    /// <summary>The shadow-map array, one layer per shadow view.</summary>
    public const string ShadowArray = "PbrShadowArray";

    /// <summary>Ray-traced ambient occlusion, one value per pixel (Rgba16Float, r = visibility).</summary>
    public const string RayTracedAo = "PbrRayTracedAo";

    /// <summary>Screen-space reflection, per pixel (Rgba16Float: rgb reflected radiance, a confidence).</summary>
    public const string SsrReflection = "PbrSsrReflection";

    /// <summary>The previous frame's HDR scene, copied for the reflection trace to read.</summary>
    public const string SsrHistory = "PbrSsrHistory";

    /// <summary>Rgba16Float: xy current UV minus previous UV, z previous device depth, w history validity.</summary>
    public const string MotionVectors = "PbrMotionVectors";

    internal const string MotionDepth = "PbrMotionDepth";

    /// <summary>The two probe irradiance atlases (Rgba16Float octahedral tiles with a 1-texel
    /// border), alternating roles each frame: one is read, the other written.</summary>
    internal static readonly string[] GiIrradiance = ["PbrGiIrradiance0", "PbrGiIrradiance1"];

    /// <summary>The two probe visibility atlases (mean and mean-squared distance), alternating
    /// like <see cref="GiIrradiance"/>.</summary>
    internal static readonly string[] GiVisibility = ["PbrGiVisibility0", "PbrGiVisibility1"];

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
    /// <summary>The current linear HDR stage, advanced by effects before bloom and tonemapping.</summary>
    public const string SceneColor = "Pbr.SceneColor";

    /// <summary>The current linear tonemapped stage, advanced before presentation encodes sRGB.</summary>
    public const string DisplayColor = "Pbr.DisplayColor";

    /// <summary>Opaque motion in top-left-origin UVs, including projection jitter: reproject with uv − xy.</summary>
    public const string MotionVectors = "Pbr.MotionVectors";

    /// <summary>Bloom mip 0, published only in frames the chain runs.</summary>
    public const string Bloom = "Pbr.Bloom";

    /// <summary>The pre-pass normal target, published only in frames the pre-pass runs.</summary>
    public const string PrepassNormal = "Pbr.Prepass.Normal";

    /// <summary>The pre-pass depth target, published together with <see cref="PrepassNormal"/>.</summary>
    public const string PrepassDepth = "Pbr.Prepass.Depth";

    /// <summary>The ray-traced ambient occlusion texture, published in frames it is computed.</summary>
    public const string RayTracedAo = "Pbr.RayTracedAo";

    /// <summary>The screen-space reflection texture, published in frames it is traced.</summary>
    public const string SsrReflection = "Pbr.SsrReflection";

    /// <summary>This frame's probe irradiance atlas, published in frames the probes update.</summary>
    public const string GiIrradiance = "Pbr.Gi.Irradiance";

    /// <summary>This frame's probe visibility atlas, published with <see cref="GiIrradiance"/>.</summary>
    public const string GiVisibility = "Pbr.Gi.Visibility";
}

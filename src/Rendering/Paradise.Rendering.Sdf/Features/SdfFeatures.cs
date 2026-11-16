using Paradise.Features;

namespace Paradise.Rendering.Sdf;

/// <summary>Declares the stable feature names and defaults used by engine configuration.</summary>
/// <remarks>Names are serialized contracts, distinct from the PBR renderer's so one process can
/// configure both paths through a shared switchboard.</remarks>
public static class SdfFeatures
{
    public static FeatureDefinition Gi { get; } = new(
        "rendering.sdfGi", true,
        "Light the SDF scene's ambient term from irradiance probes traced through the field, where the scene enables them. Off, ambient light is the sky's alone.");

    public static FeatureDefinition Scene { get; } = new(
        "rendering.sdfScene", true,
        "Sphere-trace the SDF scene into HDR color and depth. Off, nothing is traced and the frame is black.");

    public static FeatureDefinition Bloom { get; } = new(
        "rendering.sdfBloom", true,
        "Add a glow around the SDF scene's bright pixels, where the scene enables it.");

    public static FeatureDefinition Composite { get; } = new(
        "rendering.sdfComposite", true,
        "Tonemap the SDF scene onto the backbuffer. Off, the frame is never presented.");

    public static FeatureDefinition Fxaa { get; } = new(
        "rendering.sdfFxaa", true,
        "Anti-alias the tonemapped SDF frame, where the scene enables it.");

    /// <summary>All of them, in frame order, for a listing that constructs no renderer.</summary>
    public static IReadOnlyList<FeatureDefinition> All { get; } = [Gi, Scene, Bloom, Composite, Fxaa];

    /// <summary>Declares every built-in into <paramref name="switches"/>.</summary>
    public static void DeclareAll(FeatureSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        foreach (var definition in All) switches.Declare(definition);
    }
}

/// <summary>Where each built-in feature sets up, spaced so a game can land between them.</summary>
/// <remarks>This is dependency order: a feature that reads another's blackboard result comes
/// after it. Where a pass lands in the frame is its <see cref="Graph.RenderPassEvent"/>.</remarks>
public static class SdfFeatureOrder
{
    public const int Gi = 50;
    public const int Scene = 100;
    public const int Bloom = 700;
    public const int Composite = 900;
    public const int Fxaa = 950;
}

/// <summary>The names the SDF features declare their targets under.</summary>
public static class SdfTargets
{
    public const TextureFormat HdrFormat = TextureFormat.Rgba16Float;
    public const TextureFormat DepthFormat = TextureFormat.Depth32Float;

    /// <summary>Linear HDR output of the scene pass.</summary>
    public const string Hdr = "SdfHdrScene";

    /// <summary>Device depth of the traced surface, 1 where a ray reached the sky.</summary>
    /// <remarks>A host raster pass that loads this as its depth attachment composes geometry
    /// with the traced scene.</remarks>
    public const string Depth = "SdfDepth";

    public const TextureFormat ProbeFormat = TextureFormat.Rgba16Float;

    /// <summary>This frame's probe rays: a texel per ray, a row per probe, validity in alpha.</summary>
    public const string ProbeRays = "SdfProbeRays";

    /// <summary>The two probe atlases, written in turn so each frame reads the one before it.</summary>
    public static IReadOnlyList<string> Probes { get; } = ["SdfProbesA", "SdfProbesB"];

    /// <summary>The most levels the bloom chain declares; fewer at small frame sizes.</summary>
    public const int BloomLevels = 6;

    /// <summary>The bloom chain, from the half-resolution base downward.</summary>
    public static IReadOnlyList<string> Bloom { get; } =
        ["SdfBloom0", "SdfBloom1", "SdfBloom2", "SdfBloom3", "SdfBloom4", "SdfBloom5"];

    /// <summary>The tonemapped frame before presentation, linear, in <see cref="HdrFormat"/>.
    /// Declared only in frames a feature asks for it.</summary>
    public const string DisplayColor = "SdfDisplayColor";
}

/// <summary>Names under which the SDF features publish a frame's results on the
/// <see cref="Graph.FrameBlackboard"/>; a name is absent when its producer did not run.</summary>
public static class SdfResults
{
    /// <summary>The current linear HDR stage, advanced by host effects before tonemapping.</summary>
    public const string SceneColor = "Sdf.SceneColor";

    /// <summary>The probe atlas this frame updated, for passes that light from it.</summary>
    public const string Probes = "Sdf.Probes";

    /// <summary>The bloom to add to the scene, at half resolution.</summary>
    public const string Bloom = "Sdf.Bloom";

    /// <summary>The tonemapped, display-linear frame an effect resolves and presents.</summary>
    public const string DisplayColor = "Sdf.DisplayColor";
}

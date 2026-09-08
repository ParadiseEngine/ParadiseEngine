using Paradise.Features;

namespace Paradise.Rendering.Pbr;

/// <summary>Declares the stable feature names and defaults used by engine configuration.</summary>
/// <remarks>Names are serialized contracts. Both the process switch and the scene's Enabled setting
/// must permit a feature to run.</remarks>
public static class PbrFeatures
{
    /// <summary>Camera, object and skinned-vertex motion for temporal effects.</summary>
    public static FeatureDefinition MotionVectors { get; } = new(
        "rendering.motionVectors", true,
        "Screen-space motion for temporal effects. Off, temporal consumers must reject history.");

    /// <summary>Directional cascades and local-light tiles in a shared depth atlas.</summary>
    public static FeatureDefinition Shadows { get; } = new(
        "rendering.shadows", true,
        "Cascaded and local-light shadow maps in a shared atlas.");

    /// <summary>Short direct-light shadow rays through the visible depth prepass.</summary>
    public static FeatureDefinition ContactShadows { get; } = new(
        "rendering.contactShadows", true,
        "Short screen-space direct-light shadows from visible opaque geometry.");

    /// <summary>The depth + normal pre-pass every screen-space effect reads. Off, SSAO,
    /// ray-traced AO, contact shadows and screen-space reflection have nothing to read and none of them
    /// run.</summary>
    public static FeatureDefinition Prepass { get; } = new(
        "rendering.depthNormalPrepass", true,
        "The opaque depth + normal pre-pass. Off, all effects that read it stop.");

    /// <summary>Ray-traced ambient occlusion in compute, against the scene's BVH.</summary>
    public static FeatureDefinition RayTracedAo { get; } = new(
        "rendering.rayTracedAo", true,
        "Ray-traced ambient occlusion. Off, ambient is unoccluded unless SSAO is on.");

    /// <summary>Screen-space reflection over the pre-pass, colored from last frame's HDR.</summary>
    public static FeatureDefinition ScreenSpaceReflection { get; } = new(
        "rendering.screenSpaceReflection", true,
        "Screen-space reflections. Off, specular falls back to the probes or the sky.");

    /// <summary>Runtime probe global illumination: the compute ray trace and the probe
    /// blends.</summary>
    public static FeatureDefinition GlobalIllumination { get; } = new(
        "rendering.globalIllumination", true,
        "Probe global illumination. Off, indirect light is the sky ambient alone.");

    /// <summary>Forward+ froxel binning in compute. Off, the froxel grid is retracted and every
    /// light is tested against every pixel — the same picture at more cost.</summary>
    public static FeatureDefinition LightCulling { get; } = new(
        "rendering.lightCulling", true,
        "Forward+ froxel light culling. Off, every light shades every pixel.");

    /// <summary>The scene itself: sky, opaque and blended geometry into the HDR target. Off,
    /// there is no picture — which is what makes it a useful thing to switch while looking for
    /// the cost of everything else.</summary>
    public static FeatureDefinition Scene { get; } = new(
        "rendering.scene", true,
        "The main HDR pass: sky, opaque and blended geometry. Off, nothing is drawn.");

    /// <summary>Opt-in capture of the opaque scene for blend materials to sample.</summary>
    public static FeatureDefinition SceneColorCapture { get; } = new(
        "rendering.sceneColorCapture", false,
        "Copy the opaque scene so blend materials can refract it. Costs a blit and a reload per frame.");

    /// <summary>GPU luminance metering and bounded automatic or manual exposure.</summary>
    public static FeatureDefinition Exposure { get; } = new(
        "rendering.exposure", true, "GPU luminance metering and bounded automatic or manual exposure.");

    /// <summary>Thin-lens opaque depth of field.</summary>
    public static FeatureDefinition DepthOfField { get; } = new(
        "rendering.depthOfField", true, "Thin-lens opaque depth of field.");

    /// <summary>Camera and per-object motion blur from motion vectors.</summary>
    public static FeatureDefinition MotionBlur { get; } = new(
        "rendering.motionBlur", true, "Camera and per-object motion blur from motion vectors.");

    /// <summary>White balance, contrast, saturation, lift/gamma/gain and color LUT.</summary>
    public static FeatureDefinition ColorGrading { get; } = new(
        "rendering.colorGrading", true, "White balance, contrast, saturation, lift/gamma/gain and color LUT.");

    /// <summary>Radial barrel and pincushion lens distortion.</summary>
    public static FeatureDefinition LensDistortion { get; } = new(
        "rendering.lensDistortion", true, "Radial barrel and pincushion lens distortion.");

    /// <summary>Radial red and blue chromatic separation.</summary>
    public static FeatureDefinition ChromaticAberration { get; } = new(
        "rendering.chromaticAberration", true, "Radial red and blue chromatic separation.");

    /// <summary>Soft colored vignette.</summary>
    public static FeatureDefinition Vignette { get; } = new(
        "rendering.vignette", true, "Soft colored vignette.");

    /// <summary>Deterministic time-animated film grain.</summary>
    public static FeatureDefinition FilmGrain { get; } = new(
        "rendering.filmGrain", true, "Deterministic time-animated film grain.");

    /// <summary>Neighborhood-limited sharpening.</summary>
    public static FeatureDefinition Sharpening { get; } = new(
        "rendering.sharpening", true, "Neighborhood-limited sharpening.");
    /// <summary>Height fog and local participating media.</summary>
    public static FeatureDefinition Fog { get; } = new(
        "rendering.fog", true, "Height fog and shadowed participating-medium scattering.");

    /// <summary>Jittered HDR temporal accumulation with motion and depth rejection.</summary>
    public static FeatureDefinition TemporalAntiAliasing { get; } = new(
        "rendering.temporalAntiAliasing", true,
        "Temporal antialiasing. Off, the camera is unjittered and no color history accumulates.");

    /// <summary>Spatial edge filtering after tonemapping and display effects.</summary>
    public static FeatureDefinition Fxaa { get; } = new(
        "rendering.fxaa", true,
        "FXAA spatial antialiasing. Off, presentation preserves unfiltered display color.");

    /// <summary>The bloom mip chain.</summary>
    public static FeatureDefinition Bloom { get; } = new(
        "rendering.bloom", true,
        "The HDR bloom chain. Off, bright pixels do not glow.");

    /// <summary>Tonemap and present. Off, nothing reaches the backbuffer.</summary>
    public static FeatureDefinition Composite { get; } = new(
        "rendering.composite", true,
        "Tonemap the HDR scene onto the backbuffer. Off, the frame is never presented.");

    /// <summary>Output transfer and presentation after optional display effects.</summary>
    public static FeatureDefinition Presentation { get; } = new(
        "rendering.presentation", true, "Present the processed display color with one sRGB transfer.");

    /// <summary>All of them, in frame order — what a <c>--list-features</c> flag or a config-file
    /// template prints WITHOUT constructing a renderer, which on a machine with no GPU adapter is
    /// the difference between a listing and a crash. A test pins this against what
    /// <see cref="PbrRenderer"/> actually declares, so a feature added above and forgotten here
    /// fails rather than quietly going unlisted.</summary>
    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        Shadows, Prepass, MotionVectors, ContactShadows, RayTracedAo, ScreenSpaceReflection, GlobalIllumination, LightCulling,
        Scene, SceneColorCapture, Fog, TemporalAntiAliasing, Exposure, DepthOfField, MotionBlur, Bloom, Composite,
        ColorGrading, LensDistortion, ChromaticAberration, Vignette, FilmGrain, Sharpening, Fxaa, Presentation,
    ];

    /// <summary>Declares every built-in into <paramref name="switches"/>. A renderer does this
    /// for the features it adds; a host that wants the listing before it has one calls this.</summary>
    public static void DeclareAll(FeatureSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        foreach (var definition in All) switches.Declare(definition);
    }
}

/// <summary>Where each built-in feature sets up, spaced so a game can land between two of them.
///
/// <para>The order is dependency order — a feature that reads another's blackboard result comes
/// after it — and nothing else. Where a PASS lands in the frame is its
/// <see cref="Graph.RenderPassEvent"/>, which is a different question with a different answer: a
/// feature that sets up first may well declare the last pass of the frame.</para></summary>
public static class PbrFeatureOrder
{
    /// <summary>Before every built-in: a feature producing something the shadow plan or the
    /// scene consumes.</summary>
    public const int First = 0;

    public const int Shadows = 100;
    public const int Prepass = 200;
    public const int MotionVectors = 250;
    public const int ContactShadows = 275;
    public const int RayTracedAo = 300;
    public const int ScreenSpaceReflection = 400;
    public const int GlobalIllumination = 500;
    public const int LightCulling = 550;
    public const int Scene = 600;
    public const int SceneColorCapture = 700;
    public const int Fog = 710;
    public const int TemporalAntiAliasing = 720;
    public const int Exposure = 730;
    public const int DepthOfField = 740;
    public const int MotionBlur = 750;
    public const int Bloom = 800;
    public const int Composite = 900;
    public const int ColorGrading = 910;
    public const int LensDistortion = 920;
    public const int ChromaticAberration = 925;
    public const int Vignette = 930;
    public const int FilmGrain = 940;
    public const int Sharpening = 950;
    public const int AntiAliasing = 960;
    public const int Presentation = 1000;
}

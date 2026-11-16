using Paradise.Features;

namespace Paradise.Rendering.Sdf;

/// <summary>Declares the stable feature names and defaults used by engine configuration.</summary>
/// <remarks>Names are serialized contracts, distinct from the PBR renderer's so one process can
/// configure both paths through a shared switchboard.</remarks>
public static class SdfFeatures
{
    public static FeatureDefinition Scene { get; } = new(
        "rendering.sdfScene", true,
        "Sphere-trace the SDF scene into HDR color and depth. Off, nothing is traced and the frame is black.");

    public static FeatureDefinition Composite { get; } = new(
        "rendering.sdfComposite", true,
        "Tonemap the SDF scene onto the backbuffer. Off, the frame is never presented.");

    /// <summary>All of them, in frame order, for a listing that constructs no renderer.</summary>
    public static IReadOnlyList<FeatureDefinition> All { get; } = [Scene, Composite];

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
    public const int Scene = 100;
    public const int Composite = 900;
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
}

/// <summary>Names under which the SDF features publish a frame's results on the
/// <see cref="Graph.FrameBlackboard"/>; a name is absent when its producer did not run.</summary>
public static class SdfResults
{
    /// <summary>The current linear HDR stage, advanced by host effects before tonemapping.</summary>
    public const string SceneColor = "Sdf.SceneColor";
}

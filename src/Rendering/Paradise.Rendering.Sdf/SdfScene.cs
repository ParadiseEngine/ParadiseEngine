using System.Numerics;

namespace Paradise.Rendering.Sdf;

/// <summary>The analytic primitive a shape evaluates in its local space.</summary>
/// <remarks>Values are a shader contract: <c>Common/sdf.slang</c> selects on the same numbers.</remarks>
public enum SdfShapeKind : byte
{
    /// <summary>Size.X is the radius.</summary>
    Sphere = 0,

    /// <summary>Size.XYZ are the half extents, Size.W the corner radius taken out of them.</summary>
    Box = 1,

    /// <summary>Size.X is the radius, Size.Y the half length of the core segment along local Y.</summary>
    Capsule = 2,

    /// <summary>Size.X is the radius, Size.Y the half height along local Y, Size.W the edge radius.</summary>
    Cylinder = 3,

    /// <summary>Size.X is the ring radius, Size.Y the tube radius, around local Y.</summary>
    Torus = 4,

    /// <summary>The half space below the local XZ plane; its surface normal is local +Y.</summary>
    Plane = 5,
}

/// <summary>How a shape combines with the field accumulated from the shapes before it.</summary>
public enum SdfOperation : byte
{
    Union = 0,

    /// <summary>Carves the shape out of the accumulated field, which keeps its material.</summary>
    Subtraction = 1,

    Intersection = 2,
}

/// <summary>Metallic-roughness surface values with linear colors.</summary>
public readonly record struct SdfMaterial(Vector3 Albedo, float Roughness = 0.5f, float Metallic = 0f, Vector3 Emissive = default)
{
    public static SdfMaterial Default { get; } = new(new Vector3(0.8f));
}

/// <summary>One primitive of the scene's distance field.</summary>
/// <remarks>Shapes fold in list order: each combines with everything before it, so a subtraction
/// carves all earlier shapes and none of the later ones.</remarks>
public record struct SdfShape
{
    public SdfShape()
    {
    }

    public SdfShapeKind Kind { get; set; }

    /// <summary>Local-to-world transform; rotation, translation and uniform scale only.</summary>
    /// <remarks>A distance survives only a similarity transform. Shear or non-uniform scale would
    /// make the marcher overstep, so <see cref="SdfField.Set"/> rejects them.</remarks>
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    /// <summary>Local dimensions, interpreted by <see cref="Kind"/>.</summary>
    public Vector4 Size { get; set; } = Vector4.One;

    public SdfOperation Operation { get; set; }

    /// <summary>World-space blend radius of the operation; zero combines with a hard edge.</summary>
    public float Smoothness { get; set; }

    public SdfMaterial Material { get; set; } = SdfMaterial.Default;

    public static SdfShape Sphere(Vector3 center, float radius) => new()
    {
        Kind = SdfShapeKind.Sphere,
        Transform = Matrix4x4.CreateTranslation(center),
        Size = new Vector4(radius, 0f, 0f, 0f),
    };

    public static SdfShape Box(in Matrix4x4 transform, Vector3 halfExtents, float cornerRadius = 0f) => new()
    {
        Kind = SdfShapeKind.Box,
        Transform = transform,
        Size = new Vector4(halfExtents, cornerRadius),
    };

    public static SdfShape Capsule(in Matrix4x4 transform, float radius, float halfLength) => new()
    {
        Kind = SdfShapeKind.Capsule,
        Transform = transform,
        Size = new Vector4(radius, halfLength, 0f, 0f),
    };

    public static SdfShape Cylinder(in Matrix4x4 transform, float radius, float halfHeight, float edgeRadius = 0f) => new()
    {
        Kind = SdfShapeKind.Cylinder,
        Transform = transform,
        Size = new Vector4(radius, halfHeight, 0f, edgeRadius),
    };

    public static SdfShape Torus(in Matrix4x4 transform, float ringRadius, float tubeRadius) => new()
    {
        Kind = SdfShapeKind.Torus,
        Transform = transform,
        Size = new Vector4(ringRadius, tubeRadius, 0f, 0f),
    };

    /// <summary>A horizontal ground: everything below <paramref name="height"/> is solid.</summary>
    public static SdfShape Plane(float height = 0f) => new()
    {
        Kind = SdfShapeKind.Plane,
        Transform = Matrix4x4.CreateTranslation(0f, height, 0f),
        Size = Vector4.Zero,
    };
}

public enum SdfLightType : byte
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

/// <summary>One punctual light with a linear color.</summary>
/// <remarks>The renderer reads <see cref="SdfScene.Lights"/> every frame and retains no light, so a
/// host may update the lights it owns in place.</remarks>
public sealed record SdfLight
{
    public SdfLightType Type { get; set; }

    public Vector3 Position { get; set; }

    /// <summary>The direction from a lit surface toward the light: where a directional light
    /// shines from, and the reverse of the way a spot light points.</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitY;

    public Vector3 Color { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 1f;

    /// <summary>Distance at which a point or spot light's inverse-square falloff is windowed to
    /// zero; zero leaves it unbounded.</summary>
    public float Range { get; set; }

    /// <summary>Half-angle in radians within which a spot light is at full strength.</summary>
    public float InnerConeAngle { get; set; } = MathF.PI / 8f;

    /// <summary>Half-angle in radians at which a spot light reaches zero, below a right angle.</summary>
    public float OuterConeAngle { get; set; } = MathF.PI / 6f;

    /// <summary>Whether the light is occluded by marching the field toward it.</summary>
    public bool CastsShadows { get; set; } = true;
}

/// <summary>View and projection in the engine's row-vector convention, depth in [0, 1].</summary>
public struct SdfCamera
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
}

/// <summary>A three-color gradient used as background, ambient light and reflection.</summary>
public sealed record SdfSky
{
    public Vector3 Zenith { get; init; } = new(0.22f, 0.42f, 0.85f);
    public Vector3 Horizon { get; init; } = new(0.70f, 0.80f, 0.92f);
    public Vector3 Ground { get; init; } = new(0.24f, 0.22f, 0.20f);

    /// <summary>Scales the sky's contribution to surfaces without changing the background.</summary>
    public float AmbientIntensity { get; init; } = 1f;
}

/// <summary>Sphere-tracing budgets and the distance-field lighting terms.</summary>
public sealed record SdfTracing
{
    /// <summary>Field evaluations per primary ray, clamped to [1, 512].</summary>
    public int MaxSteps { get; init; } = 128;

    /// <summary>World distance past which a ray is sky, also bounded by the far plane.</summary>
    public float MaxDistance { get; init; } = 200f;

    /// <summary>Hit threshold at unit distance; it grows with distance like a pixel's footprint.</summary>
    public float SurfaceEpsilon { get; init; } = 0.001f;

    public bool Shadows { get; init; } = true;

    /// <summary>Penumbra sharpness: larger values give harder shadow edges.</summary>
    public float ShadowSharpness { get; init; } = 16f;

    /// <summary>Strength of distance-field ambient occlusion in [0, 1].</summary>
    public float AmbientOcclusion { get; init; } = 1f;
}

/// <summary>Mirror reflections of the scene, traced through the field.</summary>
/// <remarks>Off, and on surfaces rougher than <see cref="MaxRoughness"/>, the mirror direction
/// shows the sky, or under irradiance probes their light from that side.</remarks>
public sealed record SdfReflections
{
    public bool Enabled { get; init; }

    /// <summary>The roughest surface that traces its reflection, in [0, 1]. One more ray is
    /// marched and shaded for each pixel of such a surface.</summary>
    public float MaxRoughness { get; init; } = 0.5f;

    /// <summary>Field evaluations per reflection ray, clamped to [1, 256].</summary>
    public int MaxSteps { get; init; } = 64;

    /// <summary>World distance past which a reflection ray shows the sky.</summary>
    public float MaxDistance { get; init; } = 60f;

    /// <summary>Whether reflected surfaces are shadowed, at one shadow ray per light.</summary>
    public bool Shadows { get; init; } = true;
}

/// <summary>Exponential fog between the eye and what it sees, optionally thinning with height.</summary>
public sealed record SdfFog
{
    public bool Enabled { get; init; }

    public Vector3 Color { get; init; } = new(0.62f, 0.70f, 0.80f);

    /// <summary>Extinction per world unit at <see cref="BaseHeight"/>.</summary>
    public float Density { get; init; } = 0.02f;

    /// <summary>How fast density falls with height, per world unit: density is multiplied by
    /// <c>exp(-HeightFalloff * (y - BaseHeight))</c>. Zero is uniform fog.</summary>
    public float HeightFalloff { get; init; }

    public float BaseHeight { get; init; }

    /// <summary>Distance from the eye at which fog begins.</summary>
    public float StartDistance { get; init; }

    /// <summary>The most fog may hide, in [0, 1]; the sky sits at the far distance behind it.</summary>
    public float MaxOpacity { get; init; } = 1f;
}

/// <summary>Global illumination from a grid of irradiance probes traced through the field.</summary>
/// <remarks>Each frame every probe traces <see cref="RaysPerProbe"/> rays and blends what they
/// return into its history. Inside the volume the probes replace the sky's ambient light: they
/// hold the sky as the scene occludes it and the light that lit surfaces bounce, one more bounce
/// per frame. Outside it the sky's ambient light is used unchanged.</remarks>
public sealed record SdfGi
{
    public bool Enabled { get; init; }

    /// <summary>World-space corners of the box the probes cover.</summary>
    public Vector3 BoundsMin { get; init; } = new(-8f, -0.5f, -8f);

    /// <inheritdoc cref="BoundsMin"/>
    public Vector3 BoundsMax { get; init; } = new(8f, 5.5f, 8f);

    /// <summary>Distance between probes. It grows as needed to keep the grid within
    /// <see cref="MaxProbes"/>; light varies no faster than this across a surface.</summary>
    public float ProbeSpacing { get; init; } = 1f;

    /// <summary>Upper bound on the probe count, clamped to [8, <see cref="SdfGiMath.MaxProbes"/>].</summary>
    public int MaxProbes { get; init; } = 2048;

    /// <summary>Rays each probe traces per frame, clamped to [4, <see cref="SdfGiMath.MaxRaysPerProbe"/>].</summary>
    public int RaysPerProbe { get; init; } = 32;

    /// <summary>How much of a probe's history survives a frame, in [0, 0.99]. Higher is smoother
    /// and slower to follow a change in the scene.</summary>
    public float Hysteresis { get; init; } = 0.92f;

    /// <summary>Scales the probes' light on surfaces.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>How much of the light a surface receives from the probes it returns to them, in
    /// [0, 1]. Zero keeps a single bounce.</summary>
    public float Bounce { get; init; } = 1f;

    /// <summary>World distance a probe ray travels before it reads the sky.</summary>
    public float MaxRayDistance { get; init; } = 40f;

    /// <summary>Weigh each probe by whether the field is clear between it and the surface, which
    /// stops light leaking through walls thinner than the spacing at eight more field samples
    /// per pixel.</summary>
    public bool Visibility { get; init; } = true;
}

/// <summary>Glow around pixels brighter than a threshold.</summary>
public sealed record SdfBloom
{
    public bool Enabled { get; init; }

    /// <summary>Linear brightness above which a pixel blooms.</summary>
    public float Threshold { get; init; } = 1f;

    /// <summary>Width of the soft onset below the threshold.</summary>
    public float Knee { get; init; } = 0.5f;

    public float Intensity { get; init; } = 0.08f;
}

/// <summary>Fast approximate anti-aliasing of the tonemapped frame.</summary>
public sealed record SdfFxaa
{
    public bool Enabled { get; init; }

    /// <summary>Local contrast, relative to the brightest neighbor, above which an edge is smoothed.</summary>
    public float EdgeThreshold { get; init; } = 0.125f;

    /// <summary>Absolute contrast below which nothing is smoothed.</summary>
    public float MinimumThreshold { get; init; } = 0.0312f;

    /// <summary>Strength of the subpixel filter in [0, 1].</summary>
    public float SubpixelQuality { get; init; } = 0.75f;
}

public enum SdfTonemapMode : byte
{
    Linear = 0,
    Aces = 1,
}

public sealed record SdfTonemap
{
    public SdfTonemapMode Mode { get; init; } = SdfTonemapMode.Aces;
    public float Exposure { get; init; } = 1f;
}

/// <summary>The mutable CPU state consumed by one frame.</summary>
public sealed class SdfScene
{
    public SdfCamera Camera;
    public SdfSky Sky = new();
    public SdfTracing Tracing = new();
    public SdfTonemap Tonemap = new();
    public SdfReflections Reflections = new();
    public SdfFog Fog = new();
    public SdfGi Gi = new();
    public SdfBloom Bloom = new();
    public SdfFxaa Fxaa = new();

    /// <summary>The field's primitives in fold order, at most <see cref="SdfRenderer.MaxShapes"/>.</summary>
    public List<SdfShape> Shapes { get; } = [];

    /// <summary>At most <see cref="SdfRenderer.MaxLights"/> lights.</summary>
    public List<SdfLight> Lights { get; } = [];
}

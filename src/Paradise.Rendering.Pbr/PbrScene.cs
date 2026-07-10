using System.Numerics;

namespace Paradise.Rendering.Pbr;

public enum PbrLightType : byte
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

/// <summary>One punctual light (up to <see cref="FrameUniformsGpu.MaxSceneLights"/> per frame).
/// Color is LINEAR. For directionals, <see cref="Direction"/> points FROM the surface TOWARD
/// the light (the shader's L convention).</summary>
public sealed record PbrLight
{
    public PbrLightType Type { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; } = Vector3.UnitY;
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; }
    // Distance-falloff exponent for point/spot lights (Godot's LIGHT_PARAM_ATTENUATION / omni_/spot_
    // attenuation). The shader applies pow(distance, -exponent): Godot's default 1.0 is inverse-LINEAR,
    // not inverse-square. Unused by directionals (no range falloff).
    public float AttenuationExponent { get; init; } = 1f;
    public float SpotOuterDegrees { get; init; } = 45f;
    public float SpotInnerDegrees { get; init; }

    // Shadow intent. The atlas tile assignment + light-space matrices are computed by the renderer
    // each frame (it owns the atlas), so ToGpu leaves the shadow slots "off" (base tile -1) and the
    // renderer overrides SpotAngles.zw + ShadowAtlas for lights that actually get a tile.
    public bool CastsShadows { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public bool SoftShadows { get; init; }
    // Godot LIGHT_PARAM_SPECULAR: scales the specular lobe only (default 0.5 — Godot's own).
    public float Specular { get; init; } = 0.5f;

    public SceneLightGpu ToGpu() => new()
    {
        PositionAndType = new Vector4(Position, (float)Type),
        DirectionAndRange = new Vector4(Direction, Range),
        ColorAndIntensity = new Vector4(Color, Intensity),
        SpotAngles = new Vector4(SpotOuterDegrees, SpotInnerDegrees, -1f, 0f), // z=-1 → no shadow
        ShadowAtlas = new Vector4(AttenuationExponent, 0f, Specular, 0f), // x=decay, z=specular amount; renderer fills y/w for shadow tiles
    };
}

/// <summary>Hemisphere ambient (linear colors) + exposure; <see cref="Flat"/> uses the sky
/// color everywhere (the exported "flat ambient" mode).</summary>
public sealed record PbrAmbient
{
    public Vector3 Sky { get; init; } = new(0.21f, 0.23f, 0.26f);
    public Vector3 Equator { get; init; } = new(0.11f, 0.12f, 0.13f);
    public Vector3 Ground { get; init; } = new(0.05f, 0.05f, 0.05f);
    public float Exposure { get; init; } = 1f;
    public bool Flat { get; init; }
    /// <summary>Optional L2 spherical-harmonic sky irradiance (E/π), 9 RGB coefficients in
    /// Ramamoorthi order with the band factors Â=(1, 2/3, 1/4) premultiplied — the per-normal
    /// ambient Godot's sky-SH produces. When set (and not <see cref="Flat"/>), the shader uses
    /// this instead of the 3-zone hemisphere blend.</summary>
    public Vector3[]? Sh { get; init; }
}

/// <summary>Tone-mapping operator applied to the linear HDR result before the sRGB OETF. Mirrors
/// Godot's <c>Environment</c> tone mapper so the exported scene renders with the same look. Values
/// match Godot's <c>ToneMapper</c> enum ordering.</summary>
public enum PbrTonemapMode : byte
{
    Linear = 0,
    Reinhard = 1,
    Filmic = 2,
    Aces = 3,
    Agx = 4,
}

/// <summary>Scene tone-mapping parameters (exported from Godot's Environment). <see cref="Exposure"/>
/// scales the linear color before the operator; <see cref="White"/> is the operator's white point.</summary>
public sealed record PbrTonemap
{
    public PbrTonemapMode Mode { get; init; } = PbrTonemapMode.Filmic;
    public float Exposure { get; init; } = 1f;
    public float White { get; init; } = 1f;
}

/// <summary>Screen-space ambient-occlusion parameters (from Godot's Environment SSAO). When
/// <see cref="Enabled"/>, the renderer runs a world-position pre-pass and darkens ambient in
/// creases/contacts. <see cref="Radius"/> is in world units.</summary>
public sealed record PbrSsao
{
    public bool Enabled { get; init; }
    public float Radius { get; init; } = 1f;
    public float Intensity { get; init; } = 2f;
    public float Bias { get; init; } = 0.05f;
    public float Power { get; init; } = 1.5f;
}

/// <summary>Camera state: matrices via <see cref="PbrMath"/>, plus the world position the
/// shader needs for view vectors.</summary>
public struct PbrCamera
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 Position;
}

/// <summary>One uploaded draw batch: geometry handles plus the material it binds.</summary>
public sealed record PbrPrimitive(
    BufferHandle VertexBuffer,
    BufferHandle IndexBuffer,
    uint IndexCount,
    ulong VertexByteLength,
    ulong IndexByteLength,
    int MaterialId,
    // Object-space AABB (for fitting the directional shadow frustum). Default (zero) is treated as
    // "unknown" and contributes only the instance origin to the world bounds.
    Vector3 LocalMin = default,
    Vector3 LocalMax = default);

/// <summary>An uploaded mesh (one or more primitives sharing an instance transform).</summary>
public sealed record PbrMesh(PbrPrimitive[] Primitives);

/// <summary>One rendered instance of an uploaded mesh.</summary>
public sealed class PbrInstance
{
    public required PbrMesh Mesh { get; init; }
    public Matrix4x4 Model = Matrix4x4.Identity;
    public float Highlight;
}

/// <summary>Everything <see cref="PbrRenderer.RenderFrame"/> consumes for one frame. Plain CPU
/// state — mutate freely between frames.</summary>
public sealed class PbrScene
{
    public PbrCamera Camera;
    public PbrAmbient Ambient = new();
    public PbrTonemap Tonemap = new();
    public ColorRgba ClearColor = ColorRgba.CornflowerBlue;
    // Optional procedural-sky background (Godot ProceduralSkyMaterial), all colors LINEAR and
    // UNTONEMAPPED. When HasSkyBackground is set, the renderer draws a fullscreen background
    // evaluating Godot's two-part gradient (sky above the horizon, ground below) per reconstructed
    // view ray, then applies Tonemap per-pixel (Godot's order — see sky.slang).
    public bool HasSkyBackground;
    public Vector3 SkyTopColor;        // above horizon, at zenith
    public Vector3 SkyHorizonColor;    // above horizon, at the horizon
    public Vector3 SkyGroundBottom;    // below horizon, at nadir — defaults black until the exporter populates it
    public Vector3 SkyGroundHorizon;   // below horizon, at the horizon — defaults black until the exporter populates it
    public float SkySkyCurveInv = 4f;  // Godot inv_sky_curve  = 0.6 / sky_curve  (default 4)
    public float SkyGroundCurveInv = 30f; // Godot inv_ground_curve = 0.6 / ground_curve (default 30)
    // Godot Environment.reflected_light_source: ambient SPECULAR from the sky (GGX-prefiltered
    // gradient LUT × split-sum env BRDF). Only effective with HasSkyBackground.
    public bool SkyReflections;
    // ProceduralSky sun disk/halo (Godot sky_material.cpp LIGHT0 branch): to-sun direction,
    // LINEAR colour × energy, and the cosine thresholds/curve. Enabled only while a directional
    // light is on — disabling the light removes the sun from the sky, exactly like Godot.
    public bool SkySunEnabled;
    public Vector3 SkySunDirection;      // to-sun (unit)
    public Vector3 SkySunColorEnergy;    // linear colour × light energy
    public float SkySunSizeCos = 2f;     // cos(light angular distance); >1 = disk never triggers
    public float SkySunAngleMaxCos = 2f; // halo outer cosine threshold
    public float SkySunInvCurve = 24f;   // halo falloff exponent (1.6/curve^1.4, Godot's mapping)
    // Screen-space ambient occlusion. When Ssao.Enabled, the renderer runs a world-position pre-pass
    // and the shader darkens ambient in creases/contacts.
    public PbrSsao Ssao = new();
    public List<PbrLight> Lights { get; } = [];
    public List<PbrInstance> Instances { get; } = [];
}

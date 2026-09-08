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
    // Godot LIGHT_PARAM_SIZE: directional = angular diameter in DEGREES; point/spot = world
    // radius in meters. 0 = punctual (no highlight softening).
    public float Size { get; init; }
    /// <summary>How much of this light the probes bounce (Godot's <c>light_indirect_energy</c>):
    /// 1 carries it faithfully, 0 keeps it out of the indirect lighting entirely.</summary>
    public float IndirectEnergy { get; init; } = 1f;

    public SceneLightGpu ToGpu() => new()
    {
        PositionAndType = new Vector4(Position, (float)Type),
        DirectionAndRange = new Vector4(Direction, Range),
        ColorAndIntensity = new Vector4(Color, Intensity),
        SpotAngles = new Vector4(SpotOuterDegrees, SpotInnerDegrees, -1f, 0f), // z=-1 → no shadow
        ShadowAtlas = new Vector4(AttenuationExponent, 0f, Specular, 0f), // x=decay, z=specular amount; renderer fills y/w for shadow tiles
        // Directional: precompute 1−cos(angular) like Godot's light_storage; point/spot pass the
        // raw radius (the shader derives the per-fragment angular term from distance).
        SizeParams = new Vector4(
            Type == PbrLightType.Directional ? 1f - MathF.Cos(Size * (MathF.PI / 180f)) : Size,
            0f, MathF.Max(IndirectEnergy, 0f), 0f),
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

/// <summary>Bloom (HDR glow) parameters for the post-process composite. When <see cref="Enabled"/>,
/// the renderer runs a threshold + progressive dual-filter blur on the linear HDR scene target and
/// adds it back scaled by <see cref="Intensity"/>. <see cref="Threshold"/>/<see cref="Knee"/> set
/// the soft-knee brightness onset (in linear HDR luminance).</summary>
public sealed record PbrBloom
{
    public bool Enabled { get; init; }
    public float Threshold { get; init; } = 1f;
    public float Knee { get; init; } = 0.5f;
    public float Intensity { get; init; } = 0.6f;
}

/// <summary>Ray-traced ambient occlusion: hemisphere rays from the depth + normal pre-pass into the
/// scene's bounding volume hierarchy. <see cref="MaxDistance"/> is how far an occluder counts, in
/// world units; <see cref="NormalBias"/> lifts the ray origin off its surface.</summary>
public sealed record PbrRayTracedAo
{
    public bool Enabled { get; init; }
    public int RaysPerPixel { get; init; } = 4;
    public float MaxDistance { get; init; } = 1f;
    public float NormalBias { get; init; } = 0.01f;

    /// <summary>Fraction of the frame's resolution the occlusion is traced at, (0, 1]. Half
    /// resolution traces a quarter of the rays and upsamples bilinearly, which ambient occlusion
    /// — a low-frequency term — tolerates well.</summary>
    public float ResolutionScale { get; init; } = 0.5f;
}

/// <summary>Screen-space reflection: each pixel's mirror ray marched through the depth + normal
/// pre-pass, colored from the previous frame's HDR scene. Sharp — a rough surface fades it out
/// toward <see cref="MaxRoughness"/> and keeps the probe or sky specular instead.</summary>
public sealed record PbrScreenSpaceReflection
{
    public bool Enabled { get; init; }

    /// <summary>Fixed-length steps along the ray, 1 to 256; each costs a depth read per pixel.</summary>
    public int MaxSteps { get; init; } = 64;

    /// <summary>How far a reflected ray travels, in world units, before it gives up.</summary>
    public float MaxDistance { get; init; } = 12f;

    /// <summary>How far behind a surface the ray may be and still count as hitting it, in world
    /// units; too thin misses thin geometry, too thick smears a surface over what is behind it.</summary>
    public float Thickness { get; init; } = 0.3f;

    /// <summary>Roughness at which the reflection has faded out completely.</summary>
    public float MaxRoughness { get; init; } = 0.5f;

    /// <summary>Scales the reflected color.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>Fraction of the frame's resolution the reflection is traced at, (0, 1].</summary>
    public float ResolutionScale { get; init; } = 1f;
}

/// <summary>An authored probe volume: where the probe grid starts, how far apart its probes are,
/// and how many there are per axis. Overrides the fit to the static scene.</summary>
public sealed record PbrProbeVolume(Vector3 Origin, Vector3 Spacing, int CountX, int CountY, int CountZ);

/// <summary>Probe global illumination: a grid of irradiance probes updated every frame by rays
/// traced into the scene's bounding volume hierarchy, replacing the sky ambient for surfaces
/// inside the volume. Everything has a default; <see cref="Volume"/> null fits the static scene.</summary>
public sealed record PbrGi
{
    public bool Enabled { get; init; }

    /// <summary>Rays traced per probe per update, 8 to 256; the quality knob a host sets once per platform.</summary>
    public int RaysPerProbe { get; init; } = 128;

    /// <summary>How much of the previous frame's irradiance survives an update: 0.97 converges
    /// over a second or two and rejects ray noise; lower reacts faster and shimmers more.</summary>
    public float Hysteresis { get; init; } = 0.97f;

    /// <summary>Upper bound on probes when the volume is fitted automatically.</summary>
    public int MaxProbes { get; init; } = 4096;

    /// <summary>Probes traced per frame; 0 traces the whole volume every frame.</summary>
    public int ProbesPerFrame { get; init; }

    /// <summary>Scales the indirect light the probes contribute.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>Lookup bias off the shaded surface along its normal, as a fraction of probe spacing.</summary>
    public float NormalBias { get; init; } = 0.1f;

    /// <summary>Lookup bias toward the viewer, as a fraction of probe spacing.</summary>
    public float ViewBias { get; init; } = 0.3f;

    /// <summary>World-space padding around the static scene when the volume is fitted.</summary>
    public float FitMargin { get; init; } = 0.5f;

    /// <summary>An authored volume, or null to fit the static scene's bounds.</summary>
    public PbrProbeVolume? Volume { get; init; }
}

/// <summary>How an instance takes part in global illumination. Mirrors Godot's
/// <c>GeometryInstance3D.gi_mode</c> so an exporter carries it verbatim.</summary>
public enum PbrGiMode : byte
{
    /// <summary>Traced: the instance occludes and bounces light, and receives it.</summary>
    Static = 0,

    /// <summary>Receives light from the probes but is not traced — a moving prop or a
    /// character, whose bounce would lag a frame behind it anyway.</summary>
    Dynamic = 1,

    /// <summary>Neither traced nor lit by the probes: shaded with the sky ambient alone.</summary>
    Disabled = 2,
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

/// <summary>CPU milliseconds of one <see cref="PbrRenderer.RenderFrame"/>, by phase: bucketing the
/// scene, building the trace hierarchy, feature setup (uniform uploads, cluster binning, pass
/// declaration), graph compile (sorting, culling, recording), draw-ring upload, and submit.</summary>
public struct PbrCpuTimings
{
    public double Partition;
    public double TraceBuild;
    public double Setup;
    public double Compile;
    public double Upload;
    public double Submit;
    public readonly double Total => Partition + TraceBuild + Setup + Compile + Upload + Submit;
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
    Vector3 LocalMax = default,
    // Uploaded with joints/weights interleaved after the tangent (20 floats/vertex instead of 12),
    // which selects the skinned pipeline and its matching vertex layout. The two must travel
    // together: the rigid layout over a skinned buffer reads tangents as positions.
    bool Skinned = false,
    // The primitive's mesh in the renderer's trace scene, or -1 when it has none (a dynamic
    // primitive keeps the hierarchy of the geometry it was uploaded with).
    int TraceMesh = -1,
    // Mutable vertex streams cannot be culled against their upload-time bounds.
    bool Dynamic = false);

/// <summary>An uploaded mesh (one or more primitives sharing an instance transform).</summary>
public sealed record PbrMesh(PbrPrimitive[] Primitives);

/// <summary>One rendered instance of an uploaded mesh.</summary>
public sealed class PbrInstance
{
    public required PbrMesh Mesh { get; init; }
    public Matrix4x4 Model = Matrix4x4.Identity;
    public float Highlight;
    /// <summary>Index of this instance's first joint matrix in the renderer's palette buffer, or
    /// −1 for a rigid instance. Two instances of the same skinned mesh differ ONLY here — which is
    /// what lets five characters share one set of GPU buffers and still hold different poses.
    /// Write the matrices with <c>PbrRenderer.SetJointPalette</c> before the frame.</summary>
    public int JointOffset = -1;
    /// <summary>How the instance takes part in global illumination; static by default, so a scene
    /// lights itself with nothing authored.</summary>
    public PbrGiMode GiMode = PbrGiMode.Static;
}

/// <summary>Everything <see cref="PbrRenderer.RenderFrame"/> consumes for one frame. Plain CPU
/// state — mutate freely between frames.</summary>
public sealed class PbrScene
{
    public PbrCamera Camera;
    public PbrAmbient Ambient = new();
    public PbrTonemap Tonemap = new();
    public PbrBloom Bloom = new();
    /// <summary>Elapsed seconds driving time-animated procedural materials. Set each frame (pinned
    /// via <c>--anim-time</c> for deterministic screenshots/parity).</summary>
    public float ElapsedSeconds;
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
    public PbrRayTracedAo RayTracedAo = new();
    public PbrScreenSpaceReflection Ssr = new();
    public PbrGi Gi = new();
    public PbrVisibility Visibility = new();
    public List<PbrLight> Lights { get; } = [];
    public List<PbrInstance> Instances { get; } = [];
}

/// <summary>Conservative scene visibility; GPU occlusion uses the current frame and costs an additional depth pass.</summary>
public sealed class PbrVisibility
{
    public bool FrustumEnabled = true;
    public bool OcclusionEnabled;
}

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

// CPU mirrors of pbr.slang's uniform blocks. Layout rules:
//
// - Every [FieldOffset] is cross-checked against the slangc reflection at PbrRenderer init AND
//   in tests (UniformLayoutValidator) — the offsets below are never hand-trusted.
// - Matrix4x4 fields upload RAW BYTES: System.Numerics' row-major storage of its row-vector
//   convention read column-major by WGSL IS the transpose, which is exactly what mul(M, v)
//   needs for numerics-convention (v·M) math. No element shuffling anywhere.
// - Consequence for the normal matrix: WGSL wants inverse-transpose of the column-major model
//   matrix. The raw-byte duality already supplies one transpose, so the numerics-side value to
//   upload needs an explicit second transpose to cancel it: transpose(inverse(model)) — see
//   PbrMath.NormalMatrix.

/// <summary>Mirror of pbr.slang <c>SceneLight</c> (64 B, array stride 64).</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct SceneLightGpu
{
    [FieldOffset(0)] public Vector4 PositionAndType;    // xyz position, w: 0 dir / 1 point / 2 spot
    [FieldOffset(16)] public Vector4 DirectionAndRange; // xyz surface→light dir (directional), w range
    [FieldOffset(32)] public Vector4 ColorAndIntensity; // rgb linear color, w intensity
    [FieldOffset(48)] public Vector4 SpotAngles;        // x outer°, y inner°, z base shadow tile (<0 none), w strength
    [FieldOffset(64)] public Vector4 ShadowAtlas;       // x columns, y face count, z tile scale, w soft flag
}

/// <summary>Inline storage for the 8 scene lights (sequential — stride matches WGSL's 80).</summary>
[InlineArray(FrameUniformsGpu.MaxSceneLights)]
public struct SceneLightArray
{
    private SceneLightGpu _element0;
}

/// <summary>Inline storage for the per-light, per-cube-face shadow view-projection matrices
/// (<see cref="FrameUniformsGpu.MaxSceneLights"/> × 6). Stride matches WGSL's 64-byte mat4.</summary>
[InlineArray(FrameUniformsGpu.MaxSceneLights * 6)]
public struct ShadowMatrixArray
{
    private Matrix4x4 _element0;
}

/// <summary>Inline storage for the 9 L2 spherical-harmonic ambient coefficients
/// (Ramamoorthi order, band factors premultiplied at export; [0].w is the enable flag).</summary>
[InlineArray(9)]
public struct AmbientShArray
{
    private Vector4 _element0;
}

/// <summary>Mirror of pbr.slang <c>FrameUniforms</c> (3952 B).</summary>
[StructLayout(LayoutKind.Explicit, Size = 3952)]
public struct FrameUniformsGpu
{
    public const int MaxSceneLights = 8;

    [FieldOffset(0)] public Vector4 CameraPos;       // xyz world camera, w unused
    [FieldOffset(16)] public Vector4 Ambient;        // rgb sky, a exposure
    [FieldOffset(32)] public Vector4 AmbientEquator; // rgb equator, w scene light count
    [FieldOffset(48)] public Vector4 AmbientGround;  // rgb ground, w flat-ambient flag
    [FieldOffset(64)] public Vector4 AaSettings;     // y specular-AA variance, z clamp
    [FieldOffset(80)] public AmbientShArray AmbientSh;         // 9 × 16 = 144 B (L2 sky-SH irradiance)
    [FieldOffset(224)] public SceneLightArray Lights;          // 8 × 80 = 640 B
    [FieldOffset(864)] public Vector4 ShadowSettings;          // x 1/atlasSize (texel), yzw unused
    [FieldOffset(880)] public ShadowMatrixArray SceneLightShadowMatrices; // 48 × 64 = 3072 B
}

/// <summary>Mirror of pbr.slang <c>DrawUniforms</c> (208 B; ring slots stride to the device's
/// dynamic-offset alignment, ≥256).</summary>
[StructLayout(LayoutKind.Explicit, Size = 208)]
public struct DrawUniformsGpu
{
    [FieldOffset(0)] public Matrix4x4 Mvp;
    [FieldOffset(64)] public Matrix4x4 Model;
    [FieldOffset(128)] public Matrix4x4 NormalMatrix;
    [FieldOffset(192)] public Vector4 Highlight; // x weight, yzw unused
}

/// <summary>Mirror of shadow.slang <c>ShadowDrawUniforms</c>: the combined light-VP × model
/// matrix for one shadow caster. Written into the shadow draw ring at the device's dynamic-offset
/// stride (≥256), like <see cref="DrawUniformsGpu"/>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ShadowDrawUniformsGpu
{
    [FieldOffset(0)] public Matrix4x4 LightMvp;
}

/// <summary>Mirror of pbr.slang group-3 <c>SsaoUniforms</c>: params (x intensity, y radius, z bias,
/// w power) and screen (xy 1/size, zw size).</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct SsaoUniformsGpu
{
    [FieldOffset(0)] public Vector4 Params;
    [FieldOffset(16)] public Vector4 Screen;
}

/// <summary>Mirror of sky.slang <c>SkyUniforms</c>: Godot's ProceduralSkyMaterial as a per-view-ray
/// two-gradient (sky above the horizon, ground below), reconstructing the world eye direction from
/// <see cref="InvViewProj"/>. All four colours are LINEAR and untonemapped (raw sky endpoints); the
/// shader blends the gradient in linear space and applies the tone operator per-pixel — Godot's
/// order, which matters because tonemap(lerp) ≠ lerp(tonemap) for nonlinear operators.</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public struct SkyUniformsGpu
{
    [FieldOffset(0)] public Vector4 SkyTop;        // above horizon, at zenith; w: sun angleMax cos
    [FieldOffset(16)] public Vector4 SkyHorizon;   // above horizon, at the horizon; w: sun inv curve
    [FieldOffset(32)] public Vector4 GroundBottom; // below horizon, at nadir
    [FieldOffset(48)] public Vector4 GroundHorizon;// below horizon, at the horizon
    [FieldOffset(64)] public Vector4 Params;       // x: inv_sky_curve, y: inv_ground_curve, z: tonemap mode, w: tonemap exposure
    [FieldOffset(80)] public Vector4 CameraPos;    // xyz: world camera position, w: tonemap white
    [FieldOffset(96)] public Vector4 SunDirection; // xyz: to-sun (unit), w: enabled flag
    [FieldOffset(112)] public Vector4 SunColor;    // rgb: linear colour × energy, w: sun size cos
    [FieldOffset(128)] public Matrix4x4 InvViewProj;// NDC(far plane) → world, for the eye ray
}

/// <summary>Mirror of pbr.slang <c>MaterialUniforms</c> (80 B).</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct MaterialUniformsGpu
{
    [FieldOffset(0)] public Vector4 BaseColorFactor;
    [FieldOffset(16)] public float MetallicFactor;
    [FieldOffset(20)] public float RoughnessFactor;
    [FieldOffset(24)] public float NormalScale;
    [FieldOffset(28)] public float OcclusionStrength;
    [FieldOffset(32)] public Vector4 EmissiveFactor; // rgb emissive, w transmission
    [FieldOffset(48)] public Vector4 UvOffsetScale;  // xy offset, zw scale (baseColor KHR_texture_transform)
    [FieldOffset(64)] public Vector4 UvRotation;     // x radians, yzw unused
}

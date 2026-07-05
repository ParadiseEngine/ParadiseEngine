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
//   matrix, which after the raw-byte duality is plain inverse(model) on the numerics side —
//   see PbrMath.NormalMatrix.

/// <summary>Mirror of pbr.slang <c>SceneLight</c> (64 B, array stride 64).</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct SceneLightGpu
{
    [FieldOffset(0)] public Vector4 PositionAndType;    // xyz position, w: 0 dir / 1 point / 2 spot
    [FieldOffset(16)] public Vector4 DirectionAndRange; // xyz surface→light dir (directional), w range
    [FieldOffset(32)] public Vector4 ColorAndIntensity; // rgb linear color, w intensity
    [FieldOffset(48)] public Vector4 SpotAngles;        // x outer°, y inner°, zw reserved
}

/// <summary>Inline storage for the 8 scene lights (sequential — stride matches WGSL's 64).</summary>
[InlineArray(FrameUniformsGpu.MaxSceneLights)]
public struct SceneLightArray
{
    private SceneLightGpu _element0;
}

/// <summary>Mirror of pbr.slang <c>FrameUniforms</c> (592 B).</summary>
[StructLayout(LayoutKind.Explicit, Size = 592)]
public struct FrameUniformsGpu
{
    public const int MaxSceneLights = 8;

    [FieldOffset(0)] public Vector4 CameraPos;       // xyz world camera, w unused
    [FieldOffset(16)] public Vector4 Ambient;        // rgb sky, a exposure
    [FieldOffset(32)] public Vector4 AmbientEquator; // rgb equator, w scene light count
    [FieldOffset(48)] public Vector4 AmbientGround;  // rgb ground, w flat-ambient flag
    [FieldOffset(64)] public Vector4 AaSettings;     // y specular-AA variance, z clamp
    [FieldOffset(80)] public SceneLightArray Lights;
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

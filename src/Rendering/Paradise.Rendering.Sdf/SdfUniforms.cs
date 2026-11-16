using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf;

// CPU layouts match the shaders' (Common/sdfField.slang for the scene's), checked against Slang reflection at renderer initialization.
// Matrix4x4 bytes are uploaded unchanged: row-major numerics data read column-major supplies the
// transpose that the shader's mul(M, v) needs.

/// <summary>Mirror of sdfField.slang <c>SdfShape</c> (160 B, storage-buffer stride 160).</summary>
[StructLayout(LayoutKind.Sequential, Size = 160)]
public struct SdfShapeGpu
{
    public Matrix4x4 WorldToLocal;
    public Vector4 Size;             // kind-specific local dimensions
    public Vector4 Params;           // x kind, y operation, z smoothness, w uniform scale
    public Vector4 Bounds;           // xyz world center, w conservative world radius
    public Vector4 AlbedoRoughness;  // rgb linear albedo, a roughness
    public Vector4 EmissiveMetallic; // rgb linear emission, a metallic
    public Vector4 Cull;             // x ray-selection margin past the bounds, y 1 = always selected
}

/// <summary>Mirror of sdfField.slang <c>SdfLight</c> (64 B, array stride 64).</summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct SdfLightGpu
{
    public Vector4 PositionType;   // xyz position, w: 0 directional / 1 point / 2 spot
    public Vector4 DirectionRange; // xyz surface-to-light, a spot's reversed axis, w range (0 = unbounded)
    public Vector4 ColorShadow;    // rgb linear color × intensity, w: 1 casts shadows
    public Vector4 Spot;           // x cosine of the outer cone angle, y of the inner
}

[InlineArray(SdfRenderer.MaxLights)]
public struct SdfLightArray
{
    private SdfLightGpu _element0;
}

/// <summary>Mirror of sdfField.slang <c>SdfFrameUniforms</c> (784 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfFrameUniformsGpu
{
    public Matrix4x4 InvViewProj; // NDC → world, for the eye ray
    public Matrix4x4 ViewProj;    // world → clip, for the hit's device depth
    public Vector4 Counts;        // x shapes, y lights, z max steps, w max distance
    public Vector4 Trace;         // x surface epsilon, y shadow sharpness (0 = off), z AO strength, w ambient intensity
    public Vector4 SkyZenith;
    public Vector4 SkyHorizon;
    public Vector4 SkyGround;
    public Vector4 FogColor;      // rgb linear color, a density (0 = no fog)
    public Vector4 FogShape;      // x height falloff (0 = uniform), y base height, z start distance, w max opacity
    public Vector4 Reflections;   // x roughest surface that traces its reflection (negative = none), y steps, z longest ray, w 1 = shadowed
    public Vector4 CameraPosition; // xyz the eye, or a point on an orthographic camera's plane
    public SdfLightArray Lights;
}

/// <summary>Mirror of sdfGi.slang <c>SdfGiUniforms</c> (128 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfGiUniformsGpu
{
    public Vector4 Origin;        // xyz world position of probe (0, 0, 0), w spacing
    public Vector4 Counts;        // xyz probes along each axis, w probes per atlas row
    public Vector4 Params;        // x intensity (0 = no probes), y 1 = weigh probes by field visibility, z normal bias, w rays per probe
    public Vector4 Update;        // x blend weight of this frame's rays, y longest ray, z clearance at which a probe is fully valid, w bounce gain
    public Matrix4x4 RayRotation; // this frame's rotation of the ray set
}

/// <summary>Mirror of sdfBloom.slang <c>SdfBloomUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfBloomUniformsGpu
{
    public Vector4 Threshold; // x threshold, y knee
}

/// <summary>Mirror of sdfFxaa.slang <c>SdfFxaaUniforms</c> (32 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfFxaaUniformsGpu
{
    public Vector4 Screen;     // xy texel size, zw resolution
    public Vector4 Parameters; // x relative threshold, y minimum threshold, z subpixel quality
}

/// <summary>Mirror of sdfComposite.slang <c>SdfCompositeUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfCompositeUniformsGpu
{
    public Vector4 Tone; // x tonemap mode, y exposure, z bloom intensity
}

/// <summary>Validates reflected SDF uniform blocks against explicit expected layout tables.</summary>
/// <remarks>Block totals use the CPU struct sizes; field offsets and sizes are AOT-safe constants
/// that must move together with the structs above.</remarks>
public static class SdfUniformLayout
{
    private static readonly (string Name, uint Offset, uint Size)[] s_frameFields =
    [
        ("invViewProj", 0, 64),
        ("viewProj", 64, 64),
        ("counts", 128, 16),
        ("trace", 144, 16),
        ("skyZenith", 160, 16),
        ("skyHorizon", 176, 16),
        ("skyGround", 192, 16),
        ("fogColor", 208, 16),
        ("fogShape", 224, 16),
        ("reflections", 240, 16),
        ("cameraPosition", 256, 16),
        ("lights", 272, 512), // MaxLights × 64-byte SdfLight
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_giFields =
    [
        ("origin", 0, 16),
        ("counts", 16, 16),
        ("params", 32, 16),
        ("update", 48, 16),
        ("rayRotation", 64, 64),
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_bloomFields =
    [
        ("threshold", 0, 16),
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_fxaaFields =
    [
        ("screen", 0, 16),
        ("parameters", 16, 16),
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_compositeFields =
    [
        ("tone", 0, 16),
    ];

    /// <summary>Checks a program that reads the field: a scene pass or the probe trace.</summary>
    /// <param name="program">The reflected program.</param>
    /// <param name="probes">Whether the program lights from the probes, and so declares their uniforms.</param>
    /// <exception cref="InvalidOperationException">Names the first divergence.</exception>
    public static void ValidateScene(ShaderProgramDesc program, bool probes)
    {
        ValidateBlock(program, "sdfFrame", (uint)Unsafe.SizeOf<SdfFrameUniformsGpu>(), s_frameFields);
        if (probes) ValidateGi(program);
    }

    /// <summary>Checks the probe uniforms of a program that reads only them.</summary>
    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateGi(ShaderProgramDesc program) =>
        ValidateBlock(program, "sdfGi", (uint)Unsafe.SizeOf<SdfGiUniformsGpu>(), s_giFields);

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateBloom(ShaderProgramDesc program) =>
        ValidateBlock(program, "sdfBloom", (uint)Unsafe.SizeOf<SdfBloomUniformsGpu>(), s_bloomFields);

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateFxaa(ShaderProgramDesc program) =>
        ValidateBlock(program, "sdfFxaa", (uint)Unsafe.SizeOf<SdfFxaaUniformsGpu>(), s_fxaaFields);

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateComposite(ShaderProgramDesc program) =>
        ValidateBlock(program, "sdfComposite", (uint)Unsafe.SizeOf<SdfCompositeUniformsGpu>(), s_compositeFields);

    private static void ValidateBlock(
        ShaderProgramDesc program, string blockName, uint mirrorSize, (string Name, uint Offset, uint Size)[] expected)
    {
        UniformBlockDesc? block = null;
        foreach (var candidate in program.UniformBlocks)
        {
            if (!string.Equals(candidate.Name, blockName, StringComparison.Ordinal)) continue;
            block = candidate;
            break;
        }
        if (block is null)
            throw new InvalidOperationException($"SDF program reflects no uniform block named '{blockName}'.");
        if (block.SizeBytes != mirrorSize)
            throw new InvalidOperationException(
                $"Uniform block '{blockName}': CPU mirror is {mirrorSize} bytes but the shader reflects {block.SizeBytes}.");
        if (block.Fields.Length != expected.Length)
            throw new InvalidOperationException(
                $"Uniform block '{blockName}': CPU mirror expects {expected.Length} fields but the shader reflects {block.Fields.Length}.");

        for (var i = 0; i < expected.Length; i++)
        {
            var reflected = block.Fields[i];
            var (name, offset, size) = expected[i];
            if (string.Equals(reflected.Name, name, StringComparison.Ordinal) && reflected.Offset == offset && reflected.Size == size)
                continue;
            throw new InvalidOperationException(
                $"Uniform block '{blockName}' field {i}: CPU mirror expects '{name}'@{offset}+{size} " +
                $"but the shader reflects '{reflected.Name}'@{reflected.Offset}+{reflected.Size}.");
        }
    }
}

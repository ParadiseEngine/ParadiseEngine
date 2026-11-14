using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf;

// CPU layouts match sdfScene.slang, checked against Slang reflection at renderer initialization.
// Matrix4x4 bytes are uploaded unchanged: row-major numerics data read column-major supplies the
// transpose that the shader's mul(M, v) needs.

/// <summary>Mirror of sdfScene.slang <c>SdfShape</c> (144 B, storage-buffer stride 144).</summary>
[StructLayout(LayoutKind.Sequential, Size = 144)]
public struct SdfShapeGpu
{
    public Matrix4x4 WorldToLocal;
    public Vector4 Size;             // kind-specific local dimensions
    public Vector4 Params;           // x kind, y operation, z smoothness, w uniform scale
    public Vector4 Bounds;           // xyz world center, w conservative world radius
    public Vector4 AlbedoRoughness;  // rgb linear albedo, a roughness
    public Vector4 EmissiveMetallic; // rgb linear emission, a metallic
}

/// <summary>Mirror of sdfScene.slang <c>SdfLight</c> (48 B, array stride 48).</summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct SdfLightGpu
{
    public Vector4 PositionType;   // xyz position, w: 0 directional / 1 point
    public Vector4 DirectionRange; // xyz surface-to-light (directional), w range (0 = unbounded)
    public Vector4 ColorShadow;    // rgb linear color × intensity, w: 1 casts shadows
}

[InlineArray(SdfRenderer.MaxLights)]
public struct SdfLightArray
{
    private SdfLightGpu _element0;
}

/// <summary>Mirror of sdfScene.slang <c>SdfFrameUniforms</c> (592 B).</summary>
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
    public SdfLightArray Lights;
}

/// <summary>Mirror of sdfComposite.slang <c>SdfCompositeUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfCompositeUniformsGpu
{
    public Vector4 Tone; // x tonemap mode, y exposure
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
        ("lights", 208, 384), // MaxLights × 48-byte SdfLight
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_compositeFields =
    [
        ("tone", 0, 16),
    ];

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first divergence.</summary>
    public static void ValidateScene(ShaderProgramDesc program) =>
        ValidateBlock(program, "sdfFrame", (uint)Unsafe.SizeOf<SdfFrameUniformsGpu>(), s_frameFields);

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

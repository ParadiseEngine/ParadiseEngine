using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Pbr;

/// <summary>Validates the CPU uniform mirror structs against slangc's reflected byte layout —
/// the keystone that killed bank-heist's hand-packed 3824-byte frame block. Every field's
/// (name, offset, size) plus the block total must match, or init throws naming the divergence.
/// The expected tables are explicit constants (no System.Reflection — AOT-clean); drift between
/// the tables and the structs is caught because the tables are validated against the SHADER,
/// and the structs feed the GPU with the same offsets the tables assert.</summary>
public static class UniformLayoutValidator
{
    private static readonly (string Name, uint Offset, uint Size)[] s_drawFields =
    [
        ("mvp", 0, 64),
        ("model", 64, 64),
        ("normalMatrix", 128, 64),
        ("highlight", 192, 16),
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_frameFields =
    [
        ("cameraPos", 0, 16),
        ("ambient", 16, 16),
        ("ambientEquator", 32, 16),
        ("ambientGround", 48, 16),
        ("aaSettings", 64, 16),
        ("ambientSh", 80, 144), // 9 × vec4 L2 sky-SH irradiance
        ("cameraForward", 224, 16),
        ("clusterParams", 240, 16),
        ("sceneLights", 256, 5120), // 64 × 80-byte SceneLight
        ("shadowSettings", 5376, 16),
        ("sceneLightShadowMatrices", 5392, 24576), // 64 × 6 × 64-byte mat4
    ];

    private static readonly (string Name, uint Offset, uint Size)[] s_materialFields =
    [
        ("baseColorFactor", 0, 16),
        ("metallicFactor", 16, 4),
        ("roughnessFactor", 20, 4),
        ("normalScale", 24, 4),
        ("occlusionStrength", 28, 4),
        ("emissiveFactor", 32, 16),
        ("uvOffsetScale", 48, 16),
        ("uvRotation", 64, 16),
    ];

    /// <summary>Validate all three uniform blocks of the PBR program. Call once at renderer
    /// init; throws <see cref="InvalidOperationException"/> naming the first divergence.</summary>
    public static void Validate(ShaderProgramDesc program)
    {
        ValidateBlock(program, "draw", (uint)Unsafe.SizeOf<DrawUniformsGpu>(), s_drawFields);
        ValidateBlock(program, "frame", (uint)Unsafe.SizeOf<FrameUniformsGpu>(), s_frameFields);
        ValidateBlock(program, "material", (uint)Unsafe.SizeOf<MaterialUniformsGpu>(), s_materialFields);
    }

    internal static void ValidateBlock(
        ShaderProgramDesc program, string blockName, uint mirrorSize, (string Name, uint Offset, uint Size)[] expected)
    {
        UniformBlockDesc? block = null;
        foreach (var candidate in program.UniformBlocks)
        {
            if (string.Equals(candidate.Name, blockName, StringComparison.Ordinal))
            {
                block = candidate;
                break;
            }
        }
        if (block is null)
            throw new InvalidOperationException(
                $"PBR program reflects no uniform block named '{blockName}' — shader/loader drift.");

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
            if (!string.Equals(reflected.Name, name, StringComparison.Ordinal) ||
                reflected.Offset != offset ||
                reflected.Size != size)
            {
                throw new InvalidOperationException(
                    $"Uniform block '{blockName}' field {i}: CPU mirror expects '{name}'@{offset}+{size} " +
                    $"but the shader reflects '{reflected.Name}'@{reflected.Offset}+{reflected.Size}.");
            }
        }
    }
}

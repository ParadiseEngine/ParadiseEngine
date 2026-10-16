using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Pbr;

/// <summary>Validates reflected PBR block sizes and fields against explicit expected layout tables.</summary>
/// <remarks>Block totals use the CPU struct sizes; field offsets and sizes use AOT-safe constants.
/// Keep those constants synchronized with PbrUniforms; shader validation alone cannot detect a
/// CPU field-offset change that leaves the table and total size unchanged.</remarks>
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
        ("sceneLights", 256, 6144), // 64 × 96-byte SceneLight
        ("shadowSettings", 6400, 16),
        ("sceneLightShadowMatrices", 6416, 24576), // MaxShadowViews × 64-byte mat4
        ("time", 30992, 16),
        ("shadowFilter", 31008, 16),
        ("invViewProj", 31024, 64),
        ("shadowViewRects", 31088, 6144),
        ("shadowViewData", 37232, 6144),
        ("shadowViewDepth", 43376, 6144),
        ("viewProj", 49520, 64),
        ("contactShadowSettings", 49584, 16),
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
        ("procColorA", 80, 16),
        ("procColorB", 96, 16),
        ("procParams", 112, 16),
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

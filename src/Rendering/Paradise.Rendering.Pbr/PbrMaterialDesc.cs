using System.Numerics;
using Paradise.Assets.Textures;

namespace Paradise.Rendering.Pbr;

/// <summary>Material blending and coverage classification for PBR draw preparation.</summary>
public enum PbrAlphaMode : byte
{
    Opaque,
    // Conservatively excludes cutout materials from opaque occlusion and reordering;
    // fragment discard itself belongs to a custom material program.
    Mask,
    Blend,
}

/// <summary>Offset, scale and rotation of the material's base-color texture coordinates.</summary>
public readonly record struct PbrUvTransform(Vector2 Offset, Vector2 Scale, float Rotation)
{
    public static readonly PbrUvTransform Identity = new(Vector2.Zero, Vector2.One, 0f);
}

/// <summary>Renderer-owned material parameters, independent of source containers and asset documents.</summary>
/// <remarks>Loaders map cooked material documents to this description and resolve texture dependencies
/// separately through PbrMaterialTextures. The stock pipelines render double-sided; custom programs
/// implement fragment discard for masked materials.</remarks>
public sealed record PbrMaterialDesc
{
    public string? Name { get; init; }
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public float MetallicFactor { get; init; }
    public float RoughnessFactor { get; init; } = 0.8f;
    public Vector3 EmissiveFactor { get; init; }
    public float NormalScale { get; init; } = 1f;
    public float OcclusionStrength { get; init; } = 1f;
    public float TransmissionFactor { get; init; }
    public PbrAlphaMode AlphaMode { get; init; }
    public PbrUvTransform BaseColorUvTransform { get; init; } = PbrUvTransform.Identity;
    public int ProcKind { get; init; }
    public float ProcNoiseScale { get; init; } = 1f;
    public float ProcFlowSpeed { get; init; } = 1f;
    public float ProcEmissiveStrength { get; init; } = 1f;
    public Vector3 ProcColorA { get; init; }
    public Vector3 ProcColorB { get; init; }
}

/// <summary>Independently resolved, cooked KTX2 payloads for the five standard material texture slots.</summary>
/// <remarks>Empty memory selects the slot's default texture. AddMaterial consumes the payloads
/// synchronously and retains no source memory; keep it valid and unchanged until that call returns.
/// Uploaded textures are shared by content and usage until their last material is released.
/// Asset paths, identities, I/O and dependency ownership belong to the runtime loader.
/// <para>Transcoding KTX2 is CPU work AddMaterial would otherwise do on the render thread. A loader
/// calls <see cref="Transcode"/> on a worker instead and hands over the result, whose
/// <see cref="Transcoded"/> payloads AddMaterial only uploads.</para></remarks>
public readonly record struct PbrMaterialTextures
{
    public ReadOnlyMemory<byte> BaseColor { get; init; }
    public ReadOnlyMemory<byte> MetallicRoughness { get; init; }
    public ReadOnlyMemory<byte> Normal { get; init; }
    public ReadOnlyMemory<byte> Occlusion { get; init; }
    public ReadOnlyMemory<byte> Emissive { get; init; }

    /// <summary>Upload-ready payloads; a slot set here is used instead of its KTX2 bytes.</summary>
    public PbrTranscodedTextures Transcoded { get; init; }

    /// <summary>Transcodes every KTX2 slot for a device granting <paramref name="supported"/>
    /// (<see cref="IRenderer.SupportedTextureCompression"/>) and returns textures that carry only the
    /// results. Pure CPU work with no renderer access, so any thread may call it.</summary>
    /// <remarks>A payload that transcodes to nothing is dropped, so its slot takes the default texture,
    /// as AddMaterial does with such a payload.</remarks>
    public PbrMaterialTextures Transcode(TextureCompressionFormats supported) => new()
    {
        Transcoded = new PbrTranscodedTextures
        {
            BaseColor = Transcoded.BaseColor ?? Decode(BaseColor, CompressedTextureUsage.ColorSrgb, supported),
            MetallicRoughness = Transcoded.MetallicRoughness ?? Decode(MetallicRoughness, CompressedTextureUsage.LinearData, supported),
            Normal = Transcoded.Normal ?? Decode(Normal, CompressedTextureUsage.NormalMap, supported),
            Occlusion = Transcoded.Occlusion ?? Decode(Occlusion, CompressedTextureUsage.LinearData, supported),
            Emissive = Transcoded.Emissive ?? Decode(Emissive, CompressedTextureUsage.ColorSrgb, supported),
        },
    };

    private static CompressedTextureData? Decode(ReadOnlyMemory<byte> ktx2, CompressedTextureUsage usage, TextureCompressionFormats supported)
    {
        if (ktx2.IsEmpty) return null;
        var transcoded = Ktx2Transcoder.Transcode(ktx2.Span, usage, supported);
        return transcoded.IsEmpty ? null : transcoded;
    }
}

/// <summary>Already-transcoded payloads for the five standard material slots: what
/// <see cref="PbrMaterialTextures.Transcode"/> produces off the render thread.</summary>
/// <remarks>Each payload must have been transcoded for this renderer's
/// <see cref="IRenderer.SupportedTextureCompression"/> and for its slot's usage (sRGB color for base
/// color and emissive, normal map for normal, linear data otherwise).</remarks>
public readonly record struct PbrTranscodedTextures
{
    public CompressedTextureData? BaseColor { get; init; }
    public CompressedTextureData? MetallicRoughness { get; init; }
    public CompressedTextureData? Normal { get; init; }
    public CompressedTextureData? Occlusion { get; init; }
    public CompressedTextureData? Emissive { get; init; }
}

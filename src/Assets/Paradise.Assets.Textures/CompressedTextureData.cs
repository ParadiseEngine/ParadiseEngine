using Paradise.Rendering;

namespace Paradise.Assets.Textures;

/// <summary>Decoded, upload-ready texture payload: all mip levels packed into one byte array
/// with per-level windows, expressed on the engine <see cref="TextureFormat"/> contract.
/// <c>BlockWidth/BlockHeight/BytesPerBlock</c> describe the compression block geometry
/// (1×1 blocks of 4 bytes for the RGBA32 fallback path) — everything
/// <see cref="ITextureFactory.WriteTexture"/> needs without re-deriving format math.</summary>
public sealed record CompressedTextureData(
    byte[] Data,
    int Width,
    int Height,
    TextureFormat Format,
    int BlockWidth,
    int BlockHeight,
    int BytesPerBlock,
    CompressedTextureMipLevel[] MipLevels)
{
    /// <summary>True for the failure sentinel — malformed or unsupported KTX2 input yields an
    /// empty payload rather than a throw (callers substitute their 1×1 defaults).</summary>
    public bool IsEmpty => Data.Length == 0 || MipLevels.Length == 0;
}

/// <summary>One mip's window into <see cref="CompressedTextureData.Data"/>. <c>BytesPerRow</c>
/// and <c>Rows</c> are in block rows (texel rows for RGBA32) — directly what
/// <c>Queue.WriteTexture</c>'s layout wants.</summary>
/// <param name="Width">The mip's texel width.</param>
/// <param name="Height">The mip's texel height.</param>
/// <param name="CopyWidth">The copy extent: <paramref name="Width"/> rounded up to whole blocks,
/// which WebGPU requires of block-compressed copies.</param>
/// <param name="CopyHeight">The copy extent: <paramref name="Height"/> rounded up to whole blocks.</param>
public readonly record struct CompressedTextureMipLevel(
    int Width,
    int Height,
    int Offset,
    int Length,
    int BytesPerRow,
    int Rows,
    int CopyWidth,
    int CopyHeight);

/// <summary>What a texture is used for — selects the transcode target and its colour space:
/// color/emissive → sRGB; packed scalar maps (metallic-roughness/occlusion, glTF packs roughness
/// in G and metallic in B, so all four channels are kept) → linear; tangent-space normals →
/// a two-channel format (shader reconstructs Z = sqrt(1 − x² − y²)).</summary>
public enum CompressedTextureUsage
{
    ColorSrgb,
    LinearData,
    NormalMap,
}

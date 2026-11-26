namespace Paradise.Rendering;

/// <summary>Upload-ready texture payload: all mip levels packed into one byte array
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
    /// <summary>True for the failure sentinel: a decoder given malformed or unsupported input yields an
    /// empty payload rather than a throw, and callers substitute their 1×1 defaults.</summary>
    public bool IsEmpty => Data.Length == 0 || MipLevels.Length == 0;
}

/// <summary>One mip's window into <see cref="CompressedTextureData.Data"/>. <c>BytesPerRow</c>
/// is the byte pitch of a block row; <c>Rows</c> counts block rows (texel rows for RGBA32), matching
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

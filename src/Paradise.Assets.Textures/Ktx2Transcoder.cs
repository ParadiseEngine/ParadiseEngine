using Ktx;
using Paradise.Rendering;

namespace Paradise.Assets.Textures;

/// <summary>Stateless KTX2 → GPU-payload transcoder over libktx (Ktx2.NET). Two targets:
/// BC (desktop adapters with TextureCompressionBC — BC7 color/data, BC5 normals, BC1/3/4/5/7
/// passthrough for pre-compressed KTX2) and RGBA32 (the no-BC fallback; also libktx, so no
/// image-decode path exists outside KTX2). Malformed/unsupported input returns the EMPTY
/// sentinel rather than throwing — callers substitute their 1×1 defaults; a missing native
/// libktx surfaces as <see cref="DllNotFoundException"/> ("transcoding unavailable").</summary>
public static class Ktx2Transcoder
{
    private static ReadOnlySpan<byte> Ktx2Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsKtx2(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Ktx2Identifier.Length &&
        bytes[..Ktx2Identifier.Length].SequenceEqual(Ktx2Identifier);

    /// <summary>Transcode to a BC format chosen by <paramref name="usage"/> (BasisLZ/UASTC
    /// sources), or map through pre-compressed BC payloads verbatim. Mips smaller than one
    /// 4×4 block are dropped (not renderable as BC).</summary>
    public static CompressedTextureData TranscodeToBc(ReadOnlySpan<byte> ktxBytes, CompressedTextureUsage usage) =>
        Transcode(ktxBytes, usage, preferBc: true);

    /// <summary>Transcode to RGBA32 — the fallback for adapters without TextureCompressionBC.
    /// The full source mip chain is kept (no 4×4 floor).</summary>
    public static CompressedTextureData TranscodeToRgba32(ReadOnlySpan<byte> ktxBytes, CompressedTextureUsage usage) =>
        Transcode(ktxBytes, usage, preferBc: false);

    private static unsafe CompressedTextureData Transcode(
        ReadOnlySpan<byte> ktxBytes, CompressedTextureUsage usage, bool preferBc)
    {
        if (!IsKtx2(ktxBytes))
        {
            return CreateEmpty();
        }

        fixed (byte* source = ktxBytes)
        {
            Ktx2.Texture* texture = null;
            Ktx2.ErrorCode error = Ktx2.CreateFromMemory(
                in *source,
                (nuint)ktxBytes.Length,
                Ktx2.TextureCreateFlagBits.LoadImageData,
                out texture);
            if (error != Ktx2.ErrorCode.Success || texture == null)
            {
                return CreateEmpty();
            }

            try
            {
                if (texture->NumDimensions != 2 ||
                    texture->BaseWidth == 0 ||
                    texture->BaseHeight == 0 ||
                    texture->BaseDepth > 1 ||
                    texture->NumFaces > 1 ||
                    texture->NumLayers > 1)
                {
                    return CreateEmpty();
                }

                TextureFormat textureFormat;
                int bytesPerBlock;
                int blockSize;

                if (Ktx2.NeedsTranscoding(texture))
                {
                    Ktx2.TranscodeFormat transcodeFormat;
                    if (preferBc)
                    {
                        (transcodeFormat, textureFormat, bytesPerBlock) = SelectBcTarget(usage);
                        blockSize = 4;
                    }
                    else
                    {
                        transcodeFormat = Ktx2.TranscodeFormat.Rgba32;
                        // libktx's RGBA32 output stays in the source transfer function, so the
                        // sRGB-ness rides on the texture format exactly like the BC targets.
                        textureFormat = usage == CompressedTextureUsage.ColorSrgb
                            ? TextureFormat.Rgba8UnormSrgb
                            : TextureFormat.Rgba8Unorm;
                        bytesPerBlock = 4; // one texel per 1×1 "block"
                        blockSize = 1;
                    }

                    error = Ktx2.TranscodeBasis(
                        texture,
                        transcodeFormat,
                        Ktx2.TranscodeFlagBits.HighQuality | Ktx2.TranscodeFlagBits.TranscodeAlphaDataToOpaqueFormats);
                    if (error != Ktx2.ErrorCode.Success)
                    {
                        return CreateEmpty();
                    }
                }
                else if (preferBc && TryMapVkFormat(texture->VkFormat, out textureFormat, out bytesPerBlock))
                {
                    // Pre-compressed BC KTX2 passes through verbatim.
                    blockSize = 4;
                }
                else
                {
                    // Non-Basis, non-BC payloads (or BC payloads when the caller can't take
                    // BC) are out of contract.
                    return CreateEmpty();
                }

                int width = checked((int)texture->BaseWidth);
                int height = checked((int)texture->BaseHeight);
                int sourceMipCount = checked((int)Math.Max(texture->NumLevels, 1));
                int mipCount = blockSize == 4
                    ? CountRenderableBcMipLevels(width, height, sourceMipCount)
                    : sourceMipCount;

                var mipLevels = new CompressedTextureMipLevel[mipCount];
                int totalBytes = 0;
                for (uint level = 0; level < mipCount; level++)
                {
                    int mipWidth = Math.Max(1, width >> (int)level);
                    int mipHeight = Math.Max(1, height >> (int)level);
                    int rows = BlockCount(mipHeight, blockSize);
                    int bytesPerRow = BlockCount(mipWidth, blockSize) * bytesPerBlock;
                    int length = checked((int)Ktx2.GetImageSize(texture, level));
                    int expectedLength = checked(bytesPerRow * rows);
                    if (length != expectedLength)
                    {
                        return CreateEmpty();
                    }

                    mipLevels[level] = new CompressedTextureMipLevel(mipWidth, mipHeight, totalBytes, length, bytesPerRow, rows);
                    totalBytes = checked(totalBytes + length);
                }

                byte[] data = new byte[totalBytes];
                for (uint level = 0; level < mipCount; level++)
                {
                    error = Ktx2.GetImageOffset(texture, level, layer: 0, faceSlice: 0, out nuint imageOffset);
                    if (error != Ktx2.ErrorCode.Success || imageOffset > texture->DataSize)
                    {
                        return CreateEmpty();
                    }

                    CompressedTextureMipLevel mip = mipLevels[level];
                    if (texture->DataSize - imageOffset < (nuint)mip.Length)
                    {
                        return CreateEmpty();
                    }

                    new ReadOnlySpan<byte>(texture->PData + checked((int)imageOffset), mip.Length)
                        .CopyTo(data.AsSpan(mip.Offset, mip.Length));
                }

                if (blockSize == 1 && usage == CompressedTextureUsage.NormalMap)
                {
                    SwizzleTwoChannelNormals(data);
                }

                return new CompressedTextureData(data, width, height, textureFormat, blockSize, blockSize, bytesPerBlock, mipLevels);
            }
            finally
            {
                Ktx2.Destroy(texture);
            }
        }
    }

    // Maps a texture role to its Basis transcode target plus the matching engine format and
    // block size. Color/emissive and packed scalar maps go to BC7 (16 bytes/block); normal
    // maps go to two-channel BC5 (16 bytes/block).
    private static (Ktx2.TranscodeFormat Transcode, TextureFormat Format, int BytesPerBlock) SelectBcTarget(
        CompressedTextureUsage usage) =>
        usage switch
        {
            CompressedTextureUsage.ColorSrgb =>
                (Ktx2.TranscodeFormat.BC7Rgba, TextureFormat.Bc7RgbaUnormSrgb, 16),
            CompressedTextureUsage.NormalMap =>
                (Ktx2.TranscodeFormat.BC5Rg, TextureFormat.Bc5RgUnorm, 16),
            _ =>
                (Ktx2.TranscodeFormat.BC7Rgba, TextureFormat.Bc7RgbaUnorm, 16),
        };

    /// <summary>The pipeline encodes normal maps with toktx <c>--normal_mode</c> — a
    /// two-channel layout storing X in RGB and Y in ALPHA ("RRRG"). The BC5 transcode target
    /// maps that to R=X, G=Y natively; the raw RGBA32 transcode does not (it yields X,X,X,Y).
    /// Swizzle to (X, Y, 255, 255) so shaders sample R/G and reconstruct Z identically on
    /// both paths.</summary>
    private static void SwizzleTwoChannelNormals(Span<byte> rgba)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i + 1] = rgba[i + 3]; // G ← Y (alpha)
            rgba[i + 2] = 255;
            rgba[i + 3] = 255;
        }
    }

    private static int BlockCount(int pixels, int blockSize) => Math.Max(1, (pixels + blockSize - 1) / blockSize);

    private static int CountRenderableBcMipLevels(int width, int height, int sourceMipCount)
    {
        int count = 0;
        for (int level = 0; level < sourceMipCount; level++)
        {
            int mipWidth = Math.Max(1, width >> level);
            int mipHeight = Math.Max(1, height >> level);
            if (mipWidth < 4 || mipHeight < 4)
            {
                break;
            }

            count++;
        }

        return Math.Max(1, count);
    }

    private static bool TryMapVkFormat(
        Ktx2.VkFormat vkFormat,
        out TextureFormat textureFormat,
        out int bytesPerBlock)
    {
        switch (vkFormat)
        {
            case Ktx2.VkFormat.BC1RgbUnormBlock:
            case Ktx2.VkFormat.BC1RgbaUnormBlock:
                textureFormat = TextureFormat.Bc1RgbaUnorm;
                bytesPerBlock = 8;
                return true;
            case Ktx2.VkFormat.BC1RgbSrgbBlock:
            case Ktx2.VkFormat.BC1RgbaSrgbBlock:
                textureFormat = TextureFormat.Bc1RgbaUnormSrgb;
                bytesPerBlock = 8;
                return true;
            case Ktx2.VkFormat.BC3UnormBlock:
                textureFormat = TextureFormat.Bc3RgbaUnorm;
                bytesPerBlock = 16;
                return true;
            case Ktx2.VkFormat.BC3SrgbBlock:
                textureFormat = TextureFormat.Bc3RgbaUnormSrgb;
                bytesPerBlock = 16;
                return true;
            case Ktx2.VkFormat.BC4UnormBlock:
                textureFormat = TextureFormat.Bc4RUnorm;
                bytesPerBlock = 8;
                return true;
            case Ktx2.VkFormat.BC5UnormBlock:
                textureFormat = TextureFormat.Bc5RgUnorm;
                bytesPerBlock = 16;
                return true;
            case Ktx2.VkFormat.BC7UnormBlock:
                textureFormat = TextureFormat.Bc7RgbaUnorm;
                bytesPerBlock = 16;
                return true;
            case Ktx2.VkFormat.BC7SrgbBlock:
                textureFormat = TextureFormat.Bc7RgbaUnormSrgb;
                bytesPerBlock = 16;
                return true;
            default:
                textureFormat = TextureFormat.Undefined;
                bytesPerBlock = 0;
                return false;
        }
    }

    private static CompressedTextureData CreateEmpty() =>
        new([], 0, 0, TextureFormat.Undefined, 0, 0, 0, []);
}

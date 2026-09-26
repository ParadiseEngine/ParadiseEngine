using Ktx;
using Paradise.Rendering;

namespace Paradise.Assets.Textures;

/// <summary>Stateless KTX2 → GPU-payload transcoder over libktx (Ktx2.NET).</summary>
/// <remarks>Basis (BasisLZ/UASTC) sources transcode to the first block family the device grants,
/// in the order BC, ASTC, ETC2, and otherwise to RGBA32. Pre-compressed BC, ETC2/EAC and ASTC 4×4
/// payloads pass through verbatim when their family is granted. The texture's usage, not the
/// container, decides sRGB: the pipeline tags every container linear (see
/// <c>Ktx2Header.ForceLinearTransfer</c>). Malformed or unsupported input returns the empty
/// sentinel rather than throwing, so callers substitute their 1×1 defaults; a missing native
/// libktx surfaces as <see cref="DllNotFoundException"/> ("transcoding unavailable").</remarks>
public static class Ktx2Transcoder
{
    private const int CompressedBlockSize = 4;

    private static ReadOnlySpan<byte> Ktx2Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsKtx2(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Ktx2Identifier.Length &&
        bytes[..Ktx2Identifier.Length].SequenceEqual(Ktx2Identifier);

    /// <summary>Transcode Basis sources to RGBA32, keeping the full source mip chain.</summary>
    public static CompressedTextureData TranscodeToRgba32(ReadOnlySpan<byte> ktxBytes, CompressedTextureUsage usage) =>
        Transcode(ktxBytes, usage, TextureCompressionFormats.None);

    /// <summary>Transcode to the best format among the <paramref name="supported"/> block
    /// families, falling back to RGBA32.</summary>
    /// <remarks>WebGPU requires a compressed texture's base size to be whole blocks, so a Basis
    /// source whose base size is not a multiple of 4 takes the RGBA32 path; a pre-compressed one
    /// yields the empty sentinel.</remarks>
    public static unsafe CompressedTextureData Transcode(
        ReadOnlySpan<byte> ktxBytes, CompressedTextureUsage usage, TextureCompressionFormats supported)
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

                int width = checked((int)texture->BaseWidth);
                int height = checked((int)texture->BaseHeight);
                bool wholeBlocks = width % CompressedBlockSize == 0 && height % CompressedBlockSize == 0;
                bool srgb = usage == CompressedTextureUsage.ColorSrgb;
                GpuTarget target;

                if (Ktx2.NeedsTranscoding(texture))
                {
                    // libktx's RGBA32 output stays in the source transfer function, so the
                    // sRGB-ness rides on the texture format exactly like the block targets.
                    target = (wholeBlocks ? SelectCompressedTarget(usage, srgb, supported) : null)
                        ?? new GpuTarget(
                            Ktx2.TranscodeFormat.Rgba32,
                            srgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm,
                            BlockSize: 1,
                            BytesPerBlock: 4);

                    error = Ktx2.TranscodeBasis(
                        texture,
                        target.Transcode,
                        Ktx2.TranscodeFlagBits.HighQuality | Ktx2.TranscodeFlagBits.TranscodeAlphaDataToOpaqueFormats);
                    if (error != Ktx2.ErrorCode.Success)
                    {
                        return CreateEmpty();
                    }
                }
                else if (wholeBlocks && MapPrecompressed(texture->VkFormat, srgb, supported) is { } passthrough)
                {
                    target = passthrough;
                }
                else
                {
                    // Uncompressed payloads, and block formats the device cannot sample, are out of
                    // contract: libktx cannot decode a block format back to RGBA.
                    return CreateEmpty();
                }

                int mipCount = checked((int)Math.Max(texture->NumLevels, 1));
                var mipLevels = new CompressedTextureMipLevel[mipCount];
                int totalBytes = 0;
                for (uint level = 0; level < mipCount; level++)
                {
                    int mipWidth = Math.Max(1, width >> (int)level);
                    int mipHeight = Math.Max(1, height >> (int)level);
                    int blockColumns = BlockCount(mipWidth, target.BlockSize);
                    int blockRows = BlockCount(mipHeight, target.BlockSize);
                    int bytesPerRow = blockColumns * target.BytesPerBlock;
                    int length = checked((int)Ktx2.GetImageSize(texture, level));
                    if (length != checked(bytesPerRow * blockRows))
                    {
                        return CreateEmpty();
                    }

                    mipLevels[level] = new CompressedTextureMipLevel(
                        mipWidth, mipHeight, totalBytes, length, bytesPerRow, blockRows,
                        blockColumns * target.BlockSize, blockRows * target.BlockSize);
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

                if (target.BlockSize == 1 && usage == CompressedTextureUsage.NormalMap)
                {
                    SwizzleTwoChannelNormals(data);
                }

                return new CompressedTextureData(
                    data, width, height, target.Format, target.BlockSize, target.BlockSize, target.BytesPerBlock, mipLevels);
            }
            finally
            {
                Ktx2.Destroy(texture);
            }
        }
    }

    private readonly record struct GpuTarget(Ktx2.TranscodeFormat Transcode, TextureFormat Format, int BlockSize, int BytesPerBlock);

    // Color and packed scalar maps keep four channels: BC7, else ASTC 4×4 (lossless from UASTC,
    // which is an ASTC subset), else ETC2 RGBA8. Normal maps need the two-channel targets that
    // read the normal-mode layout's Y from alpha: BC5 or EAC RG11. ASTC alone cannot serve them,
    // because core WebGPU has no view swizzle to move alpha into green.
    private static GpuTarget? SelectCompressedTarget(CompressedTextureUsage usage, bool srgb, TextureCompressionFormats supported)
    {
        if (usage == CompressedTextureUsage.NormalMap)
        {
            if (supported.HasFlag(TextureCompressionFormats.Bc))
                return new(Ktx2.TranscodeFormat.BC5Rg, TextureFormat.Bc5RgUnorm, CompressedBlockSize, 16);
            if (supported.HasFlag(TextureCompressionFormats.Etc2))
                return new(Ktx2.TranscodeFormat.Etc2EacRg11, TextureFormat.EacRg11Unorm, CompressedBlockSize, 16);
            return null;
        }

        if (supported.HasFlag(TextureCompressionFormats.Bc))
            return new(Ktx2.TranscodeFormat.BC7Rgba, srgb ? TextureFormat.Bc7RgbaUnormSrgb : TextureFormat.Bc7RgbaUnorm, CompressedBlockSize, 16);
        if (supported.HasFlag(TextureCompressionFormats.Astc))
            return new(Ktx2.TranscodeFormat.Astc4X4Rgba, srgb ? TextureFormat.Astc4x4UnormSrgb : TextureFormat.Astc4x4Unorm, CompressedBlockSize, 16);
        if (supported.HasFlag(TextureCompressionFormats.Etc2))
            return new(Ktx2.TranscodeFormat.Etc2Rgba, srgb ? TextureFormat.Etc2Rgba8UnormSrgb : TextureFormat.Etc2Rgba8Unorm, CompressedBlockSize, 16);
        return null;
    }

    /// <summary>The pipeline encodes normal maps with <c>ktx create --normal-mode</c> — a
    /// two-channel layout storing X in RGB and Y in ALPHA ("RRRG"). The BC5 and EAC RG11
    /// targets map that to R=X, G=Y natively; the raw RGBA32 transcode does not (it yields
    /// X,X,X,Y). Swizzle to (X, Y, 255, 255) so shaders sample R/G and reconstruct Z identically
    /// on every path.</summary>
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

    // A pre-compressed payload keeps its block layout, so only its family must be granted. The
    // _SRGB/_UNORM half of its vkFormat is ignored in favour of the usage (see the class remarks).
    private static GpuTarget? MapPrecompressed(Ktx2.VkFormat vkFormat, bool srgb, TextureCompressionFormats supported)
    {
        (TextureFormat Linear, TextureFormat Srgb, int BytesPerBlock) format = vkFormat switch
        {
            Ktx2.VkFormat.BC1RgbUnormBlock or Ktx2.VkFormat.BC1RgbSrgbBlock or
            Ktx2.VkFormat.BC1RgbaUnormBlock or Ktx2.VkFormat.BC1RgbaSrgbBlock =>
                (TextureFormat.Bc1RgbaUnorm, TextureFormat.Bc1RgbaUnormSrgb, 8),
            Ktx2.VkFormat.BC3UnormBlock or Ktx2.VkFormat.BC3SrgbBlock =>
                (TextureFormat.Bc3RgbaUnorm, TextureFormat.Bc3RgbaUnormSrgb, 16),
            Ktx2.VkFormat.BC4UnormBlock => (TextureFormat.Bc4RUnorm, TextureFormat.Bc4RUnorm, 8),
            Ktx2.VkFormat.BC5UnormBlock => (TextureFormat.Bc5RgUnorm, TextureFormat.Bc5RgUnorm, 16),
            Ktx2.VkFormat.BC7UnormBlock or Ktx2.VkFormat.BC7SrgbBlock =>
                (TextureFormat.Bc7RgbaUnorm, TextureFormat.Bc7RgbaUnormSrgb, 16),
            Ktx2.VkFormat.Etc2R8G8B8A8UnormBlock or Ktx2.VkFormat.Etc2R8G8B8A8SrgbBlock =>
                (TextureFormat.Etc2Rgba8Unorm, TextureFormat.Etc2Rgba8UnormSrgb, 16),
            Ktx2.VkFormat.EacR11G11UnormBlock => (TextureFormat.EacRg11Unorm, TextureFormat.EacRg11Unorm, 16),
            Ktx2.VkFormat.Astc4X4UnormBlock or Ktx2.VkFormat.Astc4X4SrgbBlock =>
                (TextureFormat.Astc4x4Unorm, TextureFormat.Astc4x4UnormSrgb, 16),
            _ => (TextureFormat.Undefined, TextureFormat.Undefined, 0),
        };
        if (format.BytesPerBlock == 0 || !supported.HasFlag(TextureFormats.RequiredCompression(format.Linear)))
            return null;

        // The transcode format is unused on the passthrough path.
        return new GpuTarget(default, srgb ? format.Srgb : format.Linear, CompressedBlockSize, format.BytesPerBlock);
    }

    private static CompressedTextureData CreateEmpty() =>
        new([], 0, 0, TextureFormat.Undefined, 0, 0, 0, []);
}

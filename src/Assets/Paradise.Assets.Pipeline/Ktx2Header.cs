using System.Buffers.Binary;

namespace Paradise.Assets.Pipeline;

/// <summary>The few bytes of a KTX2 container the pipeline reads or pokes: identifier, dimensions, level count, and the colour-space tags.</summary>
public static class Ktx2Header
{
    public static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int VkFormatOffset = 12;
    private const int PixelWidthOffset = 20;
    private const int PixelHeightOffset = 24;
    private const int LevelCountOffset = 40;
    private const int DfdByteOffsetField = 48;
    private const int HeaderLength = 80;

    private const byte TransferLinear = 1;
    private const byte TransferSrgb = 2;

    public static bool IsKtx2(ReadOnlySpan<byte> bytes)
        => bytes.Length >= Identifier.Length && bytes[..Identifier.Length].SequenceEqual(Identifier);

    public static bool IsValid(byte[] bytes, out string error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        error = "";
        if (bytes.Length < HeaderLength)
        {
            error = $"file is too small ({bytes.Length} bytes).";
            return false;
        }

        if (!IsKtx2(bytes))
        {
            error = "missing KTX2 identifier.";
            return false;
        }

        var pixelWidth = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PixelWidthOffset));
        var pixelHeight = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PixelHeightOffset));
        var levelCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(LevelCountOffset));
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            error = $"invalid dimensions {pixelWidth}x{pixelHeight}.";
            return false;
        }

        if (levelCount == 0)
        {
            error = "missing mip levels.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Retags an sRGB container as linear, in place: the DFD transfer function and, for the
    /// pre-compressed block formats a DCC may hand over, the matching <c>vkFormat</c> — the
    /// two say the same thing twice, and a reader that trusts <c>vkFormat</c> (Vulkan, wgpu)
    /// would otherwise still decode sRGB (issue #212). The reason the project tags colour as
    /// linear at all is in <see cref="TextureEncodePolicy.CreateArguments"/>. No-op when the
    /// header is too short or already linear.
    /// </summary>
    public static void ForceLinearTransfer(byte[] ktx2)
    {
        ArgumentNullException.ThrowIfNull(ktx2);
        if (ktx2.Length < DfdByteOffsetField + 4) return;

        var dfdOffset = BinaryPrimitives.ReadInt32LittleEndian(ktx2.AsSpan(DfdByteOffsetField));
        // Basic DFD block: 4B totalSize, then vendor/type (4B), version/blockSize (4B),
        // colorModel (1B), colorPrimaries (1B), transferFunction (1B).
        var transferOffset = dfdOffset + 4 + 8 + 2;
        if (dfdOffset <= 0 || transferOffset >= ktx2.Length) return;

        if (ktx2[transferOffset] == TransferSrgb) ktx2[transferOffset] = TransferLinear;

        var vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(ktx2.AsSpan(VkFormatOffset));
        if (LinearCounterpart(vkFormat) is { } linear)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ktx2.AsSpan(VkFormatOffset), linear);
        }
    }

    /// <summary>The <c>_UNORM</c> twin of an <c>_SRGB</c> Vulkan format, for the families the pipeline meets; <see langword="null"/> when the format carries no transfer function (UASTC/Basis supercompressed output is <c>VK_FORMAT_UNDEFINED</c>).</summary>
    internal static uint? LinearCounterpart(uint vkFormat) => vkFormat switch
    {
        43 => 37,     // R8G8B8A8_SRGB → R8G8B8A8_UNORM
        29 => 23,     // R8G8B8_SRGB → R8G8B8_UNORM
        50 => 44,     // B8G8R8A8_SRGB → B8G8R8A8_UNORM
        132 => 131,   // BC1_RGB_SRGB_BLOCK → UNORM
        134 => 133,   // BC1_RGBA
        136 => 135,   // BC2
        138 => 137,   // BC3
        146 => 145,   // BC7
        148 => 147,   // ETC2_R8G8B8
        150 => 149,   // ETC2_R8G8B8A1
        152 => 151,   // ETC2_R8G8B8A8
        // ASTC: fourteen block sizes, UNORM then SRGB, from 157 to 184.
        >= 157 and <= 184 when (vkFormat - 157) % 2 == 1 => vkFormat - 1,
        _ => null,
    };
}

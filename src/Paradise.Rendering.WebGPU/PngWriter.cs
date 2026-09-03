using System.Buffers.Binary;
using System.IO.Compression;
using PdTextureFormat = Paradise.Rendering.TextureFormat;

namespace Paradise.Rendering.WebGPU;

/// <summary>The smallest correct PNG encoder that will do: 8-bit RGBA, no interlacing, one IDAT.
///
/// Beside <see cref="ColorReadback"/> because that is what it is for. <c>CaptureFrameAsync</c>
/// exists so a host can look at a frame it rendered — from a screenshot key, from a headless
/// smoke run in CI — and a readback nobody can open is only half of that. The engine has no other
/// image writer: the asset pipeline reads images, it does not produce them.
///
/// Deliberately not a general image library. No format choice, no metadata, no streaming, no
/// palette. A host that needs any of those has outgrown this and should take a dependency.
///
/// Writes to a <see cref="Stream"/> rather than a path, so the destination stays the caller's
/// decision — a file, a mount, or memory in a test — without this package taking a view on
/// filesystems.</summary>
public static class PngWriter
{
    /// <summary>Encode tightly-packed, top-down RGBA8.</summary>
    public static void WriteRgba(Stream destination, ReadOnlySpan<byte> pixels, uint width, uint height) =>
        Write(destination, pixels, width, height, swapRedBlue: false);

    /// <summary>Encode a captured frame, converting from its <paramref name="format"/>.</summary>
    /// <remarks>The conversion happens into the encoder's own scanline buffer.
    /// <see cref="ColorReadback.Pixels"/> belongs to the caller, and a capture that came back
    /// channel-swapped because something wrote a screenshot is the kind of bug that gets blamed on
    /// the renderer.</remarks>
    public static void Write(Stream destination, in ColorReadback readback, PdTextureFormat format) =>
        Write(
            destination,
            readback.Pixels,
            readback.Width,
            readback.Height,
            swapRedBlue: format is PdTextureFormat.Bgra8Unorm or PdTextureFormat.Bgra8UnormSrgb);

    private static void Write(
        Stream destination, ReadOnlySpan<byte> pixels, uint width, uint height, bool swapRedBlue)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var stride = checked((int)width * 4);
        if (pixels.Length < (long)stride * height)
        {
            throw new ArgumentException(
                $"Expected {(long)stride * height} bytes of 4-channel pixels, got {pixels.Length}.",
                nameof(pixels));
        }

        destination.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // deflate
        header[11] = 0; // no filtering beyond the per-scanline byte
        header[12] = 0; // no interlace
        WriteChunk(destination, "IHDR"u8, header);

        // Each scanline is prefixed with its filter type; 0 (None) keeps this honest and small.
        var raw = new byte[(stride + 1) * (long)height];
        for (var y = 0; y < height; y++)
        {
            var source = pixels.Slice(y * stride, stride);
            var target = raw.AsSpan((int)(y * (long)(stride + 1)) + 1, stride);
            source.CopyTo(target);
            if (!swapRedBlue) continue;
            for (var i = 0; i + 3 < stride; i += 4)
            {
                (target[i], target[i + 2]) = (target[i + 2], target[i]);
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        WriteChunk(destination, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(destination, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Crc32(type, data));
        stream.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = Step(crc, b);
        foreach (var b in data) crc = Step(crc, b);
        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc;
        }
    }
}

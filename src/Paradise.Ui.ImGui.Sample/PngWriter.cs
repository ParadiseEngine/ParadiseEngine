using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Paradise.Ui.ImGui.Sample;

/// <summary>The smallest correct PNG encoder that will do: 8-bit RGBA, no interlacing, one IDAT.
///
/// Here because a captured frame is only useful if somebody can LOOK at it, and the engine has no
/// image writer — the asset pipeline reads images, it does not produce them. Kept in the sample
/// rather than promoted, because "write a debug screenshot" is a sample's need and a real one
/// would want format choice, metadata and streaming.</summary>
internal static class PngWriter
{
    public static void WriteRgba(string path, ReadOnlySpan<byte> pixels, uint width, uint height)
    {
        if (pixels.Length < width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA, got {pixels.Length}.", nameof(pixels));
        }

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // deflate
        header[11] = 0; // no filtering beyond the per-scanline byte
        header[12] = 0; // no interlace
        WriteChunk(file, "IHDR"u8, header);

        // Each scanline is prefixed with its filter type; 0 (None) keeps this honest and small.
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var source = (int)(y * width * 4);
            var destination = (int)(y * (width * 4 + 1));
            raw[destination] = 0;
            pixels.Slice(source, (int)width * 4).CopyTo(raw.AsSpan(destination + 1));
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        WriteChunk(file, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(file, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crc = Crc32(type, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
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

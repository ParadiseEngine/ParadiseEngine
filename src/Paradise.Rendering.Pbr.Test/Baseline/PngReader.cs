using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Paradise.Rendering.Pbr.Test.Baseline;

/// <summary>Decodes exactly what <see cref="Paradise.Rendering.WebGPU.PngWriter"/> writes: 8-bit
/// RGBA, non-interlaced. Nothing else — a golden this cannot read is a golden this engine did not
/// produce, and quietly widening the decoder would only let a wrong file through.
///
/// It exists so the committed baseline is ONE artifact that is both machine-checkable and openable
/// in an image viewer. The alternative — a raw blob plus a hash in a separate file — has two
/// sources of truth and neither can be looked at when a test goes red at 2am.</summary>
internal static class PngReader
{
    private static ReadOnlySpan<byte> Magic => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decode to tightly-packed, top-down RGBA8.</summary>
    internal static (byte[] Pixels, uint Width, uint Height) ReadRgba(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Not a PNG.");

        uint width = 0, height = 0;
        var idat = new MemoryStream();
        var offset = 8;
        var sawHeader = false;

        while (offset + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadUInt32BigEndian(data);
                height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                var bitDepth = data[8];
                var colorType = data[9];
                var interlace = data[12];
                if (bitDepth != 8 || colorType != 6 || interlace != 0)
                    throw new NotSupportedException(
                        $"Only 8-bit RGBA non-interlaced PNG is supported (got depth={bitDepth} color={colorType} interlace={interlace}).");
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length; // length + type + data + crc
        }

        if (!sawHeader) throw new InvalidDataException("PNG has no IHDR.");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = checked((int)width * 4);
        var raw = new byte[checked(stride * (int)height)];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var filter = inflate.ReadByte();
            if (filter < 0) throw new InvalidDataException("PNG data ended mid-image.");
            inflate.ReadExactly(current, 0, stride);
            Unfilter((byte)filter, current, previous, bytesPerPixel: 4);
            current.CopyTo(raw.AsSpan(y * stride));
            (previous, current) = (current, previous);
        }

        return (raw, width, height);
    }

    /// <summary>Reverse one scanline filter in place. Filter types are PNG spec §9.2.</summary>
    private static void Unfilter(byte filter, Span<byte> line, ReadOnlySpan<byte> prior, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0: // None
                break;
            case 1: // Sub
                for (var i = bytesPerPixel; i < line.Length; i++)
                    line[i] = (byte)(line[i] + line[i - bytesPerPixel]);
                break;
            case 2: // Up
                for (var i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + prior[i]);
                break;
            case 3: // Average
                for (var i = 0; i < line.Length; i++)
                {
                    var left = i >= bytesPerPixel ? line[i - bytesPerPixel] : 0;
                    line[i] = (byte)(line[i] + ((left + prior[i]) >> 1));
                }
                break;
            case 4: // Paeth
                for (var i = 0; i < line.Length; i++)
                {
                    var a = i >= bytesPerPixel ? line[i - bytesPerPixel] : (byte)0;
                    var b = prior[i];
                    var c = i >= bytesPerPixel ? prior[i - bytesPerPixel] : (byte)0;
                    line[i] = (byte)(line[i] + Paeth(a, b, c));
                }
                break;
            default:
                throw new InvalidDataException($"Unknown PNG filter type {filter}.");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}

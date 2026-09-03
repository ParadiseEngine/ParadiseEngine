using System.Buffers.Binary;
using System.IO.Compression;
using Paradise.Rendering.WebGPU;
using PdTextureFormat = Paradise.Rendering.TextureFormat;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>The screenshot encoder, without a device.</summary>
/// <remarks>Every property here is one a caller was promised in the remarks and cannot otherwise
/// check: that a captured frame's channels come out in PNG order, and that the caller's own array
/// is not the thing that got reordered. The second is the one worth guarding — the encoder this
/// replaced swapped BGRA in place, so a host that wrote a screenshot and then used the readback
/// for anything else got it back channel-swapped, and the symptom looks like a renderer bug.</remarks>
public class PngWriterTests
{
    private const int Width = 3;
    private const int Height = 2;

    /// <summary>One distinct colour per pixel, in BGRA — what a headless capture hands back.</summary>
    private static byte[] BgraPixels()
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            pixels[i * 4 + 0] = (byte)(10 + i); // B
            pixels[i * 4 + 1] = (byte)(50 + i); // G
            pixels[i * 4 + 2] = (byte)(90 + i); // R
            pixels[i * 4 + 3] = 255;            // A
        }
        return pixels;
    }

    /// <summary>The IDAT payload, inflated back to filtered scanlines.</summary>
    private static byte[] Scanlines(byte[] png)
    {
        var offset = 8; // signature
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                using var deflated = new MemoryStream(png, offset + 8, length);
                using var inflate = new ZLibStream(deflated, CompressionMode.Decompress);
                using var raw = new MemoryStream();
                inflate.CopyTo(raw);
                return raw.ToArray();
            }
            offset += 12 + length; // length + type + data + crc
        }
        throw new InvalidOperationException("no IDAT chunk");
    }

    [Test]
    public async Task the_file_is_a_png_of_the_right_size()
    {
        using var stream = new MemoryStream();
        PngWriter.WriteRgba(stream, new byte[Width * Height * 4], Width, Height);
        var png = stream.ToArray();

        await Assert.That(png[..8]).IsEquivalentTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        // IHDR's payload starts at 16: width, height, then depth 8 and colour type 6 (RGBA).
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16))).IsEqualTo((uint)Width);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20))).IsEqualTo((uint)Height);
        await Assert.That(png[24]).IsEqualTo((byte)8);
        await Assert.That(png[25]).IsEqualTo((byte)6);
    }

    [Test]
    public async Task a_bgra_capture_is_written_in_rgba_order_and_the_caller_keeps_its_bytes()
    {
        var pixels = BgraPixels();
        var original = pixels.ToArray();
        var readback = new ColorReadback(pixels, Width, Height);

        using var stream = new MemoryStream();
        PngWriter.Write(stream, readback, PdTextureFormat.Bgra8Unorm);

        await Assert.That(pixels).IsEquivalentTo(original);

        var scanlines = Scanlines(stream.ToArray());
        await Assert.That(scanlines.Length).IsEqualTo((Width * 4 + 1) * Height);
        for (var y = 0; y < Height; y++)
        {
            var row = y * (Width * 4 + 1);
            await Assert.That(scanlines[row]).IsEqualTo((byte)0); // filter: None
            for (var x = 0; x < Width; x++)
            {
                var source = (y * Width + x) * 4;
                var target = row + 1 + x * 4;
                await Assert.That(scanlines[target + 0]).IsEqualTo(original[source + 2]); // R was B
                await Assert.That(scanlines[target + 1]).IsEqualTo(original[source + 1]); // G
                await Assert.That(scanlines[target + 2]).IsEqualTo(original[source + 0]); // B was R
                await Assert.That(scanlines[target + 3]).IsEqualTo(original[source + 3]); // A
            }
        }
    }

    // An RGBA target must NOT be swapped. The format parameter is the only thing that decides it,
    // so a writer that always swapped would pass the test above and still be wrong here.
    [Test]
    public async Task an_rgba_capture_is_written_through_unchanged()
    {
        var pixels = BgraPixels();
        using var stream = new MemoryStream();
        PngWriter.Write(stream, new ColorReadback(pixels, Width, Height), PdTextureFormat.Rgba8Unorm);

        var scanlines = Scanlines(stream.ToArray());
        await Assert.That(scanlines[1]).IsEqualTo(pixels[0]);
        await Assert.That(scanlines[3]).IsEqualTo(pixels[2]);
    }

    [Test]
    public async Task a_span_too_short_for_the_stated_size_is_refused()
    {
        using var stream = new MemoryStream();
        await Assert.That(() => PngWriter.WriteRgba(stream, new byte[Width * Height * 4 - 1], Width, Height))
            .Throws<ArgumentException>();
    }
}

using System.IO;
using Paradise.Rendering;

namespace Paradise.Assets.Textures.Test;

/// <summary>Golden coverage over two tiny checked-in KTX2 fixtures generated once with the
/// pipeline's own toktx (8×8 + full mip chain; BasisLZ sRGB color and UASTC linear normal —
/// the two encoder presets ToktxKtx2 embeds into GLBs). Transcoding is pure CPU (libktx);
/// tests skip (not fail) where the native library can't load.</summary>
public class Ktx2TranscoderTests
{
    private static byte[]? LoadFixtureOrSkip(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Force the native load once; a host without libktx skips the whole suite.
            _ = Ktx2Transcoder.TranscodeToBc(bytes, CompressedTextureUsage.ColorSrgb);
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"libktx not loadable on this host: {ex.Message}");
            return null;
        }
        return bytes;
    }

    [Test]
    public async Task is_ktx2_sniffs_magic()
    {
        var ktx2 = new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A, 1 };
        await Assert.That(Ktx2Transcoder.IsKtx2(ktx2)).IsTrue();
        await Assert.That(Ktx2Transcoder.IsKtx2([1, 2, 3])).IsFalse();
        await Assert.That(Ktx2Transcoder.IsKtx2([])).IsFalse();
    }

    [Test]
    public async Task color_fixture_transcodes_to_bc7_srgb_with_two_renderable_mips()
    {
        var bytes = LoadFixtureOrSkip("color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToBc(bytes, CompressedTextureUsage.ColorSrgb);
        await Assert.That(data.IsEmpty).IsFalse();
        // Color textures upload under a LINEAR BC7 format; the sRGB decode is done in-shader
        // (pbr.slang srgbToLinear). The raw texels stay sRGB-valued; only the format tag is *Unorm.
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Bc7RgbaUnorm);
        await Assert.That(data.Width).IsEqualTo(8);
        await Assert.That(data.Height).IsEqualTo(8);
        await Assert.That(data.BlockWidth).IsEqualTo(4);
        await Assert.That(data.BytesPerBlock).IsEqualTo(16);

        // 8×8 source with a full toktx mip chain (8,4,2,1) — BC keeps only mips ≥ one 4×4
        // block: 8×8 (2×2 blocks, 64 B) and 4×4 (1 block, 16 B).
        await Assert.That(data.MipLevels.Length).IsEqualTo(2);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(8, 8, 0, 64, 32, 2));
        await Assert.That(data.MipLevels[1]).IsEqualTo(new CompressedTextureMipLevel(4, 4, 64, 16, 16, 1));
        await Assert.That(data.Data.Length).IsEqualTo(80);
    }

    [Test]
    public async Task normal_fixture_transcodes_to_bc5_two_channel()
    {
        var bytes = LoadFixtureOrSkip("normal-linear-uastc.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToBc(bytes, CompressedTextureUsage.NormalMap);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Bc5RgUnorm);
        await Assert.That(data.MipLevels.Length).IsEqualTo(2);
        await Assert.That(data.BytesPerBlock).IsEqualTo(16);
    }

    [Test]
    public async Task linear_data_usage_selects_non_srgb_bc7()
    {
        var bytes = LoadFixtureOrSkip("color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToBc(bytes, CompressedTextureUsage.LinearData);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Bc7RgbaUnorm);
    }

    [Test]
    public async Task rgba32_fallback_keeps_the_full_mip_chain()
    {
        var bytes = LoadFixtureOrSkip("color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToRgba32(bytes, CompressedTextureUsage.ColorSrgb);
        await Assert.That(data.IsEmpty).IsFalse();
        // Color uploads under a linear format; sRGB decode is in-shader (see BC test above).
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Rgba8Unorm);
        await Assert.That(data.BlockWidth).IsEqualTo(1);
        await Assert.That(data.BytesPerBlock).IsEqualTo(4);

        // No 4×4 floor on the RGBA path: 8,4,2,1 all kept, tightly packed texel rows.
        await Assert.That(data.MipLevels.Length).IsEqualTo(4);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(8, 8, 0, 256, 32, 8));
        await Assert.That(data.MipLevels[1]).IsEqualTo(new CompressedTextureMipLevel(4, 4, 256, 64, 16, 4));
        await Assert.That(data.MipLevels[2]).IsEqualTo(new CompressedTextureMipLevel(2, 2, 320, 16, 8, 2));
        await Assert.That(data.MipLevels[3]).IsEqualTo(new CompressedTextureMipLevel(1, 1, 336, 4, 4, 1));
        await Assert.That(data.Data.Length).IsEqualTo(340);
    }

    [Test]
    public async Task rgba32_fallback_for_linear_usages_is_non_srgb()
    {
        var bytes = LoadFixtureOrSkip("normal-linear-uastc.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToRgba32(bytes, CompressedTextureUsage.NormalMap);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Rgba8Unorm);
        await Assert.That(data.MipLevels.Length).IsEqualTo(4);
    }

    [Test]
    public async Task rgba32_normal_fallback_swizzles_to_bc5_channel_semantics()
    {
        // toktx --normal_mode stores X in RGB and Y in ALPHA ("RRRG"); BC5 transcoding maps
        // that to R=X, G=Y natively but raw RGBA32 yields (X,X,X,Y). The fallback swizzles to
        // (X, Y, 255, 255) so shaders sample R/G + reconstruct Z identically on both paths.
        // The fixture's texel (0,0) was authored ≈(127,127) in X/Y before encoding.
        var bytes = LoadFixtureOrSkip("normal-linear-uastc.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToRgba32(bytes, CompressedTextureUsage.NormalMap);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(Math.Abs(data.Data[0] - 127)).IsLessThan(12); // R ← X
        await Assert.That(Math.Abs(data.Data[1] - 127)).IsLessThan(12); // G ← Y (from alpha)
        await Assert.That((int)data.Data[2]).IsEqualTo(255);            // B forced opaque-up
        await Assert.That((int)data.Data[3]).IsEqualTo(255);            // A forced
    }

    [Test]
    public async Task malformed_inputs_return_the_empty_sentinel()
    {
        var garbage = new byte[64];
        var result = Ktx2Transcoder.TranscodeToBc(garbage, CompressedTextureUsage.ColorSrgb);
        await Assert.That(result.IsEmpty).IsTrue();

        // Valid magic followed by garbage: libktx parse failure → empty, not a throw.
        var truncated = new byte[32];
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(truncated, 0);
        try
        {
            var result2 = Ktx2Transcoder.TranscodeToBc(truncated, CompressedTextureUsage.ColorSrgb);
            await Assert.That(result2.IsEmpty).IsTrue();
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"libktx not loadable on this host: {ex.Message}");
        }
    }
}

using System.IO;
using Paradise.Rendering;

namespace Paradise.Assets.Textures.Test;

/// <summary>Golden coverage over tiny checked-in KTX2 fixtures: 8×8 BasisLZ sRGB color and UASTC
/// normal-mode normals with full mip chains (the pipeline's two encoder presets), 12×20 and 10×10
/// UASTC colour (block-aligned and not), and raw 8×8 BC7/ASTC payloads tagged UNORM the way
/// <c>Ktx2Header.ForceLinearTransfer</c> leaves them. Transcoding is pure CPU (libktx); tests
/// skip (not fail) where the native library can't load.</summary>
public class Ktx2TranscoderTests
{
    private const TextureCompressionFormats Mobile = TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc;
    private const TextureCompressionFormats All = TextureCompressionFormats.Bc | Mobile;

    private static byte[]? LoadFixtureOrSkip(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Force the native load once; a host without libktx skips the whole suite.
            _ = Ktx2Transcoder.TranscodeToRgba32(bytes, CompressedTextureUsage.ColorSrgb);
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
    public async Task bc_keeps_the_full_mip_chain_with_block_padded_copy_extents()
    {
        var bytes = LoadFixtureOrSkip("color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.Transcode(bytes, CompressedTextureUsage.ColorSrgb, TextureCompressionFormats.Bc);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Bc7RgbaUnormSrgb);
        await Assert.That(data.Width).IsEqualTo(8);
        await Assert.That(data.Height).IsEqualTo(8);
        await Assert.That(data.BlockWidth).IsEqualTo(4);
        await Assert.That(data.BytesPerBlock).IsEqualTo(16);

        // 8,4,2,1: mips below one block still occupy (and are copied as) a whole 4×4 block.
        await Assert.That(data.MipLevels.Length).IsEqualTo(4);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(8, 8, 0, 64, 32, 2, 8, 8));
        await Assert.That(data.MipLevels[1]).IsEqualTo(new CompressedTextureMipLevel(4, 4, 64, 16, 16, 1, 4, 4));
        await Assert.That(data.MipLevels[2]).IsEqualTo(new CompressedTextureMipLevel(2, 2, 80, 16, 16, 1, 4, 4));
        await Assert.That(data.MipLevels[3]).IsEqualTo(new CompressedTextureMipLevel(1, 1, 96, 16, 16, 1, 4, 4));
        await Assert.That(data.Data.Length).IsEqualTo(112);
    }

    [Test]
    public async Task non_power_of_two_mips_copy_whole_blocks()
    {
        var bytes = LoadFixtureOrSkip("color-12x20-uastc.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.Transcode(bytes, CompressedTextureUsage.ColorSrgb, Mobile);
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Astc4x4UnormSrgb);
        // 12×20 → 6×10 → 3×5 → 1×2 → 1×1.
        await Assert.That(data.MipLevels.Length).IsEqualTo(5);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(12, 20, 0, 240, 48, 5, 12, 20));
        await Assert.That(data.MipLevels[1]).IsEqualTo(new CompressedTextureMipLevel(6, 10, 240, 96, 32, 3, 8, 12));
        await Assert.That(data.MipLevels[2]).IsEqualTo(new CompressedTextureMipLevel(3, 5, 336, 32, 16, 2, 4, 8));
        await Assert.That(data.MipLevels[3]).IsEqualTo(new CompressedTextureMipLevel(1, 2, 368, 16, 16, 1, 4, 4));
    }

    [Test]
    public async Task a_base_size_that_is_not_whole_blocks_takes_the_rgba32_path()
    {
        // WebGPU refuses to create a compressed texture whose base size is not whole blocks.
        var bytes = LoadFixtureOrSkip("color-10x10-uastc.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.Transcode(bytes, CompressedTextureUsage.ColorSrgb, All);
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Rgba8UnormSrgb);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(10, 10, 0, 400, 40, 10, 10, 10));
    }

    [Test]
    [Arguments(TextureCompressionFormats.Bc | TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc, CompressedTextureUsage.ColorSrgb, TextureFormat.Bc7RgbaUnormSrgb)]
    [Arguments(TextureCompressionFormats.Bc | TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc, CompressedTextureUsage.NormalMap, TextureFormat.Bc5RgUnorm)]
    [Arguments(TextureCompressionFormats.Bc, CompressedTextureUsage.LinearData, TextureFormat.Bc7RgbaUnorm)]
    [Arguments(TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc, CompressedTextureUsage.ColorSrgb, TextureFormat.Astc4x4UnormSrgb)]
    [Arguments(TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc, CompressedTextureUsage.LinearData, TextureFormat.Astc4x4Unorm)]
    [Arguments(TextureCompressionFormats.Etc2 | TextureCompressionFormats.Astc, CompressedTextureUsage.NormalMap, TextureFormat.EacRg11Unorm)]
    [Arguments(TextureCompressionFormats.Etc2, CompressedTextureUsage.ColorSrgb, TextureFormat.Etc2Rgba8UnormSrgb)]
    [Arguments(TextureCompressionFormats.Etc2, CompressedTextureUsage.LinearData, TextureFormat.Etc2Rgba8Unorm)]
    [Arguments(TextureCompressionFormats.Astc, CompressedTextureUsage.NormalMap, TextureFormat.Rgba8Unorm)]
    [Arguments(TextureCompressionFormats.None, CompressedTextureUsage.ColorSrgb, TextureFormat.Rgba8UnormSrgb)]
    public async Task basis_sources_take_the_best_granted_family(
        TextureCompressionFormats supported, CompressedTextureUsage usage, TextureFormat expected)
    {
        var bytes = LoadFixtureOrSkip(usage == CompressedTextureUsage.NormalMap ? "normal-linear-uastc.ktx2" : "color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.Transcode(bytes, usage, supported);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(expected);
        await Assert.That((supported & TextureFormats.RequiredCompression(data.Format)) == TextureFormats.RequiredCompression(data.Format)).IsTrue();
    }

    [Test]
    public async Task rgba32_keeps_the_full_mip_chain()
    {
        var bytes = LoadFixtureOrSkip("color-srgb-etc1s.ktx2");
        if (bytes is null) return;

        var data = Ktx2Transcoder.TranscodeToRgba32(bytes, CompressedTextureUsage.ColorSrgb);
        await Assert.That(data.IsEmpty).IsFalse();
        await Assert.That(data.Format).IsEqualTo(TextureFormat.Rgba8UnormSrgb);
        await Assert.That(data.BlockWidth).IsEqualTo(1);
        await Assert.That(data.BytesPerBlock).IsEqualTo(4);

        await Assert.That(data.MipLevels.Length).IsEqualTo(4);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(8, 8, 0, 256, 32, 8, 8, 8));
        await Assert.That(data.MipLevels[1]).IsEqualTo(new CompressedTextureMipLevel(4, 4, 256, 64, 16, 4, 4, 4));
        await Assert.That(data.MipLevels[2]).IsEqualTo(new CompressedTextureMipLevel(2, 2, 320, 16, 8, 2, 2, 2));
        await Assert.That(data.MipLevels[3]).IsEqualTo(new CompressedTextureMipLevel(1, 1, 336, 4, 4, 1, 1, 1));
        await Assert.That(data.Data.Length).IsEqualTo(340);
    }

    [Test]
    public async Task rgba32_normal_fallback_swizzles_to_two_channel_semantics()
    {
        // ktx create --normal-mode stores X in RGB and Y in ALPHA ("RRRG"); BC5 and EAC RG11 map
        // that to R=X, G=Y natively but raw RGBA32 yields (X,X,X,Y). The fallback swizzles to
        // (X, Y, 255, 255) so shaders sample R/G + reconstruct Z identically on every path.
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
    [Arguments("bc7-unorm-8x8.ktx2", TextureCompressionFormats.Bc, CompressedTextureUsage.ColorSrgb, TextureFormat.Bc7RgbaUnormSrgb)]
    [Arguments("bc7-unorm-8x8.ktx2", TextureCompressionFormats.Bc, CompressedTextureUsage.LinearData, TextureFormat.Bc7RgbaUnorm)]
    [Arguments("astc4x4-unorm-8x8.ktx2", TextureCompressionFormats.Astc, CompressedTextureUsage.ColorSrgb, TextureFormat.Astc4x4UnormSrgb)]
    [Arguments("astc4x4-unorm-8x8.ktx2", TextureCompressionFormats.Astc, CompressedTextureUsage.LinearData, TextureFormat.Astc4x4Unorm)]
    public async Task precompressed_payloads_pass_through_in_the_usage_colour_space(
        string fixture, TextureCompressionFormats supported, CompressedTextureUsage usage, TextureFormat expected)
    {
        // The pipeline retags sRGB containers UNORM for Godot, so the container cannot say
        // whether a colour texture is sRGB; the material slot does.
        var bytes = LoadFixtureOrSkip(fixture);
        if (bytes is null) return;

        var data = Ktx2Transcoder.Transcode(bytes, usage, supported);
        await Assert.That(data.Format).IsEqualTo(expected);
        await Assert.That(data.MipLevels[0]).IsEqualTo(new CompressedTextureMipLevel(8, 8, 0, 64, 32, 2, 8, 8));
    }

    [Test]
    public async Task a_precompressed_payload_outside_the_granted_families_is_empty()
    {
        var bytes = LoadFixtureOrSkip("bc7-unorm-8x8.ktx2");
        if (bytes is null) return;

        await Assert.That(Ktx2Transcoder.Transcode(bytes, CompressedTextureUsage.ColorSrgb, Mobile).IsEmpty).IsTrue();
    }

    [Test]
    public async Task malformed_inputs_return_the_empty_sentinel()
    {
        var garbage = new byte[64];
        var result = Ktx2Transcoder.Transcode(garbage, CompressedTextureUsage.ColorSrgb, All);
        await Assert.That(result.IsEmpty).IsTrue();

        // Valid magic followed by garbage: libktx parse failure → empty, not a throw.
        var truncated = new byte[32];
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(truncated, 0);
        try
        {
            var result2 = Ktx2Transcoder.Transcode(truncated, CompressedTextureUsage.ColorSrgb, All);
            await Assert.That(result2.IsEmpty).IsTrue();
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"libktx not loadable on this host: {ex.Message}");
        }
    }
}

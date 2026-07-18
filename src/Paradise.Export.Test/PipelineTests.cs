using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Paradise.Export.Pipeline;

namespace Paradise.Export.Tests;

// Engine-neutral coverage of the asset-pipeline logic: GLB container round-trip, `ktx create`
// argument building + preset selection, KTX2 validation, and executable resolution.
public class PipelineTests
{
    [Test]
    public async Task glb_round_trips_json_and_bin()
    {
        var gltf = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["meshes"] = new JsonArray() };
        byte[] bin = { 1, 2, 3, 4, 5 };
        string path = Path.Combine(Path.GetTempPath(), $"paradise_glb_{Guid.NewGuid():N}.glb");
        try
        {
            GlbBinary.Write(path, gltf, bin);
            bool read = GlbBinary.TryRead(path, out JsonObject readGltf, out byte[] readBin);

            await Assert.That(read).IsTrue();
            await Assert.That((string?)readGltf["asset"]!["version"]).IsEqualTo("2.0");
            // BIN chunk is padded to a 4-byte boundary; the original bytes are preserved as a prefix.
            await Assert.That(readBin.Length).IsGreaterThanOrEqualTo(bin.Length);
            await Assert.That(readBin[0]).IsEqualTo((byte)1);
            await Assert.That(readBin[4]).IsEqualTo((byte)5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task ktx_create_args_srgb_default_vs_normal_preset()
    {
        string srgb = KtxCreate.BuildCreateArguments(KtxCreate.TextureEncodingPreset.UastcColorSrgb, "out.ktx2", "in.png");
        // Colour texels are sRGB-encoded but the container is tagged LINEAR — the workaround for
        // Godot's KHR_texture_basisu double sRGB decode (see BuildCreateArguments).
        await Assert.That(srgb).Contains("--format R8G8B8A8_UNORM");
        await Assert.That(srgb).Contains("--assign-tf linear");
        await Assert.That(srgb).Contains("--encode uastc");
        await Assert.That(srgb).Contains("--generate-mipmap");
        // v5 positional order: input before output.
        await Assert.That(srgb.IndexOf("in.png", StringComparison.Ordinal))
            .IsLessThan(srgb.IndexOf("out.ktx2", StringComparison.Ordinal));

        string normal = KtxCreate.BuildCreateArguments(KtxCreate.TextureEncodingPreset.UastcNormalLinear, "out.ktx2", "in.png");
        await Assert.That(normal).Contains("--normal-mode");
        await Assert.That(normal).Contains("--encode uastc");
        await Assert.That(normal).Contains("--format R8G8B8A8_UNORM");
        await Assert.That(normal).Contains("--assign-tf linear");
    }

    [Test]
    public async Task preset_inferred_from_image_name()
    {
        await Assert.That(KtxCreate.PresetFromImageName(new JsonObject { ["name"] = "Wall_Normal" }))
            .IsEqualTo(KtxCreate.TextureEncodingPreset.UastcNormalLinear);
        await Assert.That(KtxCreate.PresetFromImageName(new JsonObject { ["name"] = "Steel_Roughness" }))
            .IsEqualTo(KtxCreate.TextureEncodingPreset.UastcDataLinear);
        await Assert.That(KtxCreate.PresetFromImageName(new JsonObject { ["name"] = "Hero_Albedo" }))
            .IsEqualTo(KtxCreate.TextureEncodingPreset.UastcColorSrgb);
    }

    // 8x8 transparent RGBA PNG (stdlib-generated once); enough for a real encode.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAADUlEQVR4nGNgGAUgAAABCAABgukLHQAAAABJRU5ErkJggg==";

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "third_party")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    [Test]
    public async Task convert_embedded_textures_end_to_end_with_the_vendored_ktx()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null || KtxCreate.FindKtx(repoRoot) is null)
        {
            Skip.Test("ktx (KTX-Software v5) not available — vendored tool missing on this platform.");
        }

        byte[] png = Convert.FromBase64String(TinyPngBase64);
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["images"] = new JsonArray(new JsonObject
            {
                ["name"] = "Wall_Albedo",
                ["mimeType"] = "image/png",
                ["bufferView"] = 0,
            }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }),
            ["bufferViews"] = new JsonArray(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = 0,
                ["byteLength"] = png.Length,
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = png.Length }),
        };

        string path = Path.Combine(Path.GetTempPath(), $"paradise_ktx5_{Guid.NewGuid():N}.glb");
        try
        {
            GlbBinary.Write(path, gltf, png);
            var errors = new System.Collections.Generic.List<string>();
            KtxCreate.ConversionResult result = KtxCreate.ConvertEmbeddedTextures(
                path, repoRoot, error: errors.Add);

            await Assert.That(string.Join("\n", errors)).IsEqualTo("");
            await Assert.That(result).IsEqualTo(KtxCreate.ConversionResult.ConvertedAllTextures);

            await Assert.That(GlbBinary.TryRead(path, out JsonObject converted, out byte[] bin)).IsTrue();
            var image = (JsonObject)converted["images"]![0]!;
            await Assert.That((string?)image["mimeType"]).IsEqualTo("image/ktx2");
            var texture = (JsonObject)converted["textures"]![0]!;
            await Assert.That(texture["extensions"]?["KHR_texture_basisu"]?["source"]).IsNotNull();

            // The rewritten buffer view must hold a valid KTX2 payload.
            var view = (JsonObject)converted["bufferViews"]![(int)image["bufferView"]!.GetValue<int>()]!;
            int offset = (int?)view["byteOffset"] ?? 0;
            int length = (int)view["byteLength"]!.GetValue<int>();
            byte[] ktx2 = bin.AsSpan(offset, length).ToArray();
            await Assert.That(KtxCreate.IsValidKtx2(ktx2, out string validationError)).IsTrue();
            await Assert.That(validationError).IsEqualTo("");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task ktx2_validation_rejects_garbage_accepts_valid_header()
    {
        await Assert.That(KtxCreate.IsValidKtx2(new byte[10], out _)).IsFalse();

        byte[] valid = new byte[80];
        byte[] identifier = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
        Array.Copy(identifier, valid, identifier.Length);
        BitConverter.GetBytes(4u).CopyTo(valid, 20); // pixelWidth
        BitConverter.GetBytes(4u).CopyTo(valid, 24); // pixelHeight
        BitConverter.GetBytes(1u).CopyTo(valid, 40); // levelCount
        await Assert.That(KtxCreate.IsValidKtx2(valid, out _)).IsTrue();
    }

    [Test]
    public async Task quote_argument_handles_plain_and_trailing_backslash()
    {
        await Assert.That(ProcessTools.QuoteArgument("plain")).IsEqualTo("\"plain\"");
        // A trailing backslash must be doubled so it can't escape the closing quote on Windows.
        await Assert.That(ProcessTools.QuoteArgument(@"C:\dir\").EndsWith("\\\\\"")).IsTrue();
    }

    [Test]
    public async Task corrupt_glb_returns_false_instead_of_throwing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"paradise_bad_{Guid.NewGuid():N}.glb");
        File.WriteAllText(path, "not a glb");
        try
        {
            await Assert.That(GlbBinary.TryRead(path, out _, out _)).IsFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task find_executable_prefers_env_path_when_present()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"paradise_tool_{Guid.NewGuid():N}");
        File.WriteAllText(fake, "");
        try
        {
            await Assert.That(ProcessTools.FindExecutable(fake, Array.Empty<string>(), "does-not-exist-xyz"))
                .IsEqualTo(fake);
            await Assert.That(ProcessTools.FindExecutable(null, Array.Empty<string>(), "definitely-not-a-real-binary-xyz"))
                .IsNull();
        }
        finally
        {
            if (File.Exists(fake)) File.Delete(fake);
        }
    }
}

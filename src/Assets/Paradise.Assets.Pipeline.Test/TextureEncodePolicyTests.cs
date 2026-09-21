using System.Text.Json.Nodes;

using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class TextureEncodePolicyTests
{
    [Test]
    public async Task colour_is_tagged_linear_and_normal_maps_get_normal_mode()
    {
        var srgb = TextureEncodePolicy.CreateArguments(TextureEncodingPreset.UastcColorSrgb, "out.ktx2", "in.png", TextureQuality.Full);
        // Colour texels are sRGB-encoded but the container is tagged LINEAR — the workaround for
        // Godot's KHR_texture_basisu double sRGB decode (see CreateArguments).
        await Assert.That(srgb).Contains("--format R8G8B8A8_UNORM");
        await Assert.That(srgb).Contains("--assign-tf linear");
        await Assert.That(srgb).Contains("--encode uastc");
        await Assert.That(srgb).Contains("--generate-mipmap");
        await Assert.That(srgb).Contains("--uastc-quality 2");
        await Assert.That(srgb).DoesNotContain("--normal-mode");
        // v5 positional order: input before output.
        await Assert.That(srgb.IndexOf("in.png", StringComparison.Ordinal)).IsLessThan(srgb.IndexOf("out.ktx2", StringComparison.Ordinal));

        var normal = TextureEncodePolicy.CreateArguments(TextureEncodingPreset.UastcNormalLinear, "out.ktx2", "in.png", TextureQuality.Full);
        await Assert.That(normal).Contains("--normal-mode");
        await Assert.That(normal).Contains("--assign-tf linear");
    }

    [Test]
    public async Task fast_quality_is_a_different_argv()
    {
        var full = TextureEncodePolicy.CreateArguments(TextureEncodingPreset.UastcColorSrgb, "o", "i", TextureQuality.Full);
        var fast = TextureEncodePolicy.CreateArguments(TextureEncodingPreset.UastcColorSrgb, "o", "i", TextureQuality.Fast);

        await Assert.That(fast).Contains("--uastc-quality 0");
        await Assert.That(fast).IsNotEqualTo(full);
    }

    [Test]
    [Arguments("Wall_NormalMap", TextureEncodingPreset.UastcNormalLinear)]
    [Arguments("wall-normal", TextureEncodingPreset.UastcNormalLinear)]
    [Arguments("WallNormal", TextureEncodingPreset.UastcNormalLinear)]
    [Arguments("Rock_AO", TextureEncodingPreset.UastcDataLinear)]
    [Arguments("Rock_ao_2k", TextureEncodingPreset.UastcDataLinear)]
    [Arguments("Steel_Roughness", TextureEncodingPreset.UastcDataLinear)]
    [Arguments("Cloth_Mask", TextureEncodingPreset.UastcColorLinear)]
    [Arguments("Chaos_Albedo", TextureEncodingPreset.UastcColorSrgb)]
    [Arguments("Damask", TextureEncodingPreset.UastcColorSrgb)]
    [Arguments("Abnormal", TextureEncodingPreset.UastcColorSrgb)]
    [Arguments("Aorta", TextureEncodingPreset.UastcColorSrgb)]
    [Arguments("", TextureEncodingPreset.UastcColorSrgb)]
    public async Task the_preset_matches_whole_name_tokens_not_substrings(string name, TextureEncodingPreset expected)
    {
        await Assert.That(TextureEncodePolicy.PresetFromImageName(name)).IsEqualTo(expected);
        await Assert.That(TextureEncodePolicy.PresetFromImageName(new JsonObject { ["name"] = name })).IsEqualTo(expected);
    }

    [Test]
    public async Task material_slots_decide_the_preset_and_the_more_specific_slot_wins()
    {
        var gltf = new JsonObject
        {
            ["images"] = new JsonArray(new JsonObject { ["name"] = "a" }, new JsonObject { ["name"] = "b" }, new JsonObject { ["name"] = "c" }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }, new JsonObject { ["source"] = 1 }, new JsonObject { ["source"] = 2 }),
            ["materials"] = new JsonArray(
                new JsonObject
                {
                    ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 }, ["metallicRoughnessTexture"] = new JsonObject { ["index"] = 1 } },
                    ["normalTexture"] = new JsonObject { ["index"] = 2 },
                },
                // The same image bound as base colour elsewhere keeps the data preset.
                new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 1 } } }),
        };

        var presets = TextureEncodePolicy.MaterialPresets(gltf);

        await Assert.That(presets[0]).IsEqualTo(TextureEncodingPreset.UastcColorSrgb);
        await Assert.That(presets[1]).IsEqualTo(TextureEncodingPreset.UastcDataLinear);
        await Assert.That(presets[2]).IsEqualTo(TextureEncodingPreset.UastcNormalLinear);
    }
}

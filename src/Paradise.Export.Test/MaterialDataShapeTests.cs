using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Paths;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins the serialized shape of a material document the Godot adapter produces, and the
// material-field path mapping.
public class MaterialDataShapeTests
{
    [Test]
    public async Task material_serializes_with_expected_pbr_shape()
    {
        var material = new LevelMaterialData
        {
            Path = "materials/steel.json",
            Name = "Steel",
            BaseColorFactor = Color32.FromRgba(0.5f, 0.5f, 0.5f, 1f),
            BaseColorTexture = "textures/steel_albedo.png",
            MetallicFactor = 1f,
            RoughnessFactor = 0.3f,
            EmissiveFactor = Color32.FromRgba(0f, 0f, 0f, 1f),
            AlphaMode = "Opaque",
            RenderQueue = -1,
        };

        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(material))!;
        await Assert.That((string?)json["Name"]).IsEqualTo("Steel");
        await Assert.That((float)json["MetallicFactor"]!).IsEqualTo(1f);
        await Assert.That((float)json["RoughnessFactor"]!).IsEqualTo(0.3f);
        await Assert.That((string?)json["AlphaMode"]).IsEqualTo("Opaque");
        await Assert.That((string?)json["BaseColorTexture"]).IsEqualTo("textures/steel_albedo.png");
        // Color32 packs to 8 bits: 0.5 → byte 128 → 128/255 (not exactly 0.5).
        await Assert.That((float)json["BaseColorFactor"]!["r"]!).IsEqualTo(128f / 255f);
        // A null texture slot is included (key present) AND emitted as JSON null — not "" or {}.
        // STJ represents a JSON null as a C#-null node, so assert presence + null value.
        await Assert.That(json.AsObject().ContainsKey("NormalTexture")).IsTrue();
        await Assert.That(json["NormalTexture"]).IsNull();
    }

    [Test]
    public async Task material_field_strips_path_and_extension()
    {
        await Assert.That(ExportPaths.MaterialFileField("res://materials/Steel.tres")).IsEqualTo("materials/Steel.json");
        await Assert.That(ExportPaths.MaterialFileField("Brick")).IsEqualTo("materials/Brick.json");
    }
}

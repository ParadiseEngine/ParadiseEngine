using System.Numerics;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins the serialization format details called out in MIGRATION.md's validation strategy:
// matrix column-major order, Color32 { r,g,b,a } shape, and enum-by-name.
public class ExportJsonFormatTests
{
    [Test]
    public async Task matrix_is_written_column_major()
    {
        // CreateTranslation puts the translation in M41/M42/M43 (row-vector convention).
        // Column-major flattening => translation lands at flat indices 3, 7, 11.
        var transform = new TransformComponentData { World = Matrix4x4.CreateTranslation(1f, 2f, 3f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(transform))!;
        JsonArray? m = json["World"] as JsonArray;

        await Assert.That(m).IsNotNull();
        await Assert.That(m!.Count).IsEqualTo(16);
        await Assert.That((float)m[3]!).IsEqualTo(1f);
        await Assert.That((float)m[7]!).IsEqualTo(2f);
        await Assert.That((float)m[11]!).IsEqualTo(3f);
        await Assert.That((float)m[15]!).IsEqualTo(1f);
    }

    [Test]
    public async Task color32_is_written_as_a_hex_string()
    {
        var light = new SceneLightData { Color = Color32.FromRgba(1f, 0f, 0f, 1f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(light))!;

        // The packed int, spelled. Alpha is always present, so the literal is a fixed nine
        // characters and a reader never has to guess whether a short form meant opaque.
        await Assert.That((string?)json["Color"]).IsEqualTo("#FF0000FF");
    }

    [Test]
    public async Task a_legacy_rgba_object_still_reads()
    {
        // The read half stays generous deliberately: every committed document, and every host not
        // yet updated, keeps loading while the format is migrated. Only WRITING changed.
        var material = ExportJsonReader.ReadMaterial(
            """{"BaseColorFactor": {"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0}}""");

        await Assert.That(material.BaseColorFactor.Rgba)
            .IsEqualTo(Color32.FromRgba(1f, 0f, 0f, 1f).Rgba);
    }

    [Test]
    public async Task a_hex_colour_reads_back_as_the_same_value()
    {
        var material = ExportJsonReader.ReadMaterial("""{"BaseColorFactor": "#FF0000FF"}""");

        await Assert.That(material.BaseColorFactor.Rgba)
            .IsEqualTo(Color32.FromRgba(1f, 0f, 0f, 1f).Rgba);
    }

    [Test]
    public async Task enums_are_written_by_name()
    {
        var body = new RigidbodyComponentData { BodyType = PhysicsBodyType.Kinematic };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(body))!;
        await Assert.That((string?)json["BodyType"]).IsEqualTo("Kinematic");
    }

    [Test]
    public async Task vector3_is_written_as_array()
    {
        var light = new SceneLightData { Position = new Vector3(1f, 2f, -10f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(light))!;
        JsonArray? p = json["Position"] as JsonArray;

        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Count).IsEqualTo(3);
        await Assert.That((float)p[2]!).IsEqualTo(-10f);
    }
}

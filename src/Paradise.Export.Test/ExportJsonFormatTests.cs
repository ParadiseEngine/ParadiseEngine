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
        var entity = new LevelEntityData { WorldMatrix = Matrix4x4.CreateTranslation(1f, 2f, 3f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(entity))!;
        JsonArray? m = json["WorldMatrix"] as JsonArray;

        await Assert.That(m).IsNotNull();
        await Assert.That(m!.Count).IsEqualTo(16);
        await Assert.That((float)m[3]!).IsEqualTo(1f);
        await Assert.That((float)m[7]!).IsEqualTo(2f);
        await Assert.That((float)m[11]!).IsEqualTo(3f);
        await Assert.That((float)m[15]!).IsEqualTo(1f);
    }

    [Test]
    public async Task color32_is_written_as_rgba_object()
    {
        var camera = new CameraData { BackgroundColor = Color32.FromRgba(1f, 0f, 0f, 1f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(camera))!;
        JsonObject? c = json["BackgroundColor"] as JsonObject;

        await Assert.That(c).IsNotNull();
        await Assert.That((float)c!["r"]!).IsEqualTo(1f);
        await Assert.That((float)c["g"]!).IsEqualTo(0f);
        await Assert.That((float)c["b"]!).IsEqualTo(0f);
        await Assert.That((float)c["a"]!).IsEqualTo(1f);
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
        var camera = new CameraData { Position = new Vector3(1f, 2f, -10f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(camera))!;
        JsonArray? p = json["Position"] as JsonArray;

        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Count).IsEqualTo(3);
        await Assert.That((float)p[2]!).IsEqualTo(-10f);
    }
}

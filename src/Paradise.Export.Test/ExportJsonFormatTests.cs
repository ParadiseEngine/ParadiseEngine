using Paradise.Export.Data;
using Paradise.Export.Serialization;
using System.Text.Json.Nodes;

namespace Paradise.Export.Tests;

// Pins the serialization format details the envelope still owns. Since contract v6 no matrices,
// vectors or enums cross the wire in ENGINE types — entity payloads are opaque, and the game's
// generated readers own their wire shapes (covered by the authoring generator tests). What
// remains engine-owned is the material document, whose Color32 spelling is pinned here.
public class ExportJsonFormatTests
{
    [Test]
    public async Task color32_is_written_as_a_hex_string()
    {
        var material = new LevelMaterialData { BaseColorFactor = Color32.FromRgba(1f, 0f, 0f, 1f) };
        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(material))!;

        // The packed int, spelled. Alpha is always present, so the literal is a fixed nine
        // characters and a reader never has to guess whether a short form meant opaque.
        await Assert.That((string?)json["BaseColorFactor"]).IsEqualTo("#FF0000FF");
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
}

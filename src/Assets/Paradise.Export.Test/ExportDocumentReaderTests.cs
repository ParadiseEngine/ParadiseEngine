using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>
/// A built <c>.material</c> keeps its name under every build profile, so the text is what says
/// whether it is TOML or JSON. One reader tells them apart, so no host dispatches on extension.
/// </summary>
public class ExportDocumentReaderTests
{
    [Test]
    public async Task a_json_material_reads_as_json()
    {
        var material = ExportDocumentReader.ReadMaterial("""  {"Name": "rust", "BaseColorFactor": "#FF0000FF"}""");

        await Assert.That(material.Name).IsEqualTo("rust");
        await Assert.That(material.BaseColorFactor.Rgba).IsEqualTo(Color32.FromRgba(1f, 0f, 0f, 1f).Rgba);
    }

    [Test]
    public async Task a_toml_material_reads_as_toml()
    {
        var material = ExportDocumentReader.ReadMaterial("Name = \"rust\"\nBaseColorFactor = \"#FF0000FF\"\n");

        await Assert.That(material.Name).IsEqualTo("rust");
        await Assert.That(material.BaseColorFactor.Rgba).IsEqualTo(Color32.FromRgba(1f, 0f, 0f, 1f).Rgba);
    }

    /// <summary>Extraction writes glTF's doubleSided into every material document (#355); the reader
    /// used to drop it, so every material rendered double-sided whatever it said.</summary>
    [Test]
    public async Task double_sided_reads_from_either_format_and_defaults_to_true_when_absent()
    {
        var toml = ExportDocumentReader.ReadMaterial("Name = \"wall\"\nDoubleSided = false\n");
        var json = ExportDocumentReader.ReadMaterial("""{"Name": "wall", "DoubleSided": false}""");
        var absent = ExportDocumentReader.ReadMaterial("Name = \"wall\"\n");

        await Assert.That(toml.DoubleSided).IsFalse();
        await Assert.That(json.DoubleSided).IsFalse();
        await Assert.That(absent.DoubleSided).IsTrue();
    }

    [Test]
    public async Task a_byte_order_mark_does_not_hide_the_brace()
    {
        await Assert.That(ExportDocumentReader.IsJson("﻿{}")).IsTrue();
        await Assert.That(ExportDocumentReader.IsJson("﻿Name = \"x\"")).IsFalse();
        await Assert.That(ExportDocumentReader.IsJson("")).IsFalse();
    }
}

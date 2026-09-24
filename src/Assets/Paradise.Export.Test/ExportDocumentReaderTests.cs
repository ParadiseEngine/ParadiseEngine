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

    [Test]
    public async Task a_byte_order_mark_does_not_hide_the_brace()
    {
        await Assert.That(ExportDocumentReader.IsJson("﻿{}")).IsTrue();
        await Assert.That(ExportDocumentReader.IsJson("﻿Name = \"x\"")).IsFalse();
        await Assert.That(ExportDocumentReader.IsJson("")).IsFalse();
    }
}

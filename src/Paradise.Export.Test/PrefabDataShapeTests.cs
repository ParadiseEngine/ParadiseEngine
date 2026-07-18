using System.Collections.Generic;
using System.Text.Json.Nodes;
using Paradise.Export.Data;
using Paradise.Export.Paths;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

// Pins the prefab template document shape the Godot adapter produces, and the prefab field mapping.
public class PrefabDataShapeTests
{
    [Test]
    public async Task prefab_field_strips_res_and_prefabs_prefix()
    {
        await Assert.That(ExportPaths.PrefabFileField("res://prefabs/models/hero.tscn"))
            .IsEqualTo("prefabs/models/hero.json");
        await Assert.That(ExportPaths.PrefabFileField("res://characters/orc.tscn"))
            .IsEqualTo("prefabs/characters/orc.json");
        await Assert.That(ExportPaths.PrefabFileField("Box")).IsEqualTo("prefabs/Box.json");
    }

    [Test]
    public async Task prefab_template_serializes_with_entities()
    {
        var template = new PrefabTemplateData
        {
            DisplayName = "Hero",
            Prefab = "models/hero.glb",
            PrefabAssetPath = "prefabs/models/hero.json",
            PrefabGuid = "uid://abc123",
            PrefabAssetType = ".tscn",
            Materials = new List<string?> { "materials/skin.json" },
            Entities = new List<LevelEntityData> { new() { Id = "Hero", Kind = "Character" } },
        };

        JsonNode json = JsonNode.Parse(ExportJsonWriter.SerializeToString(template))!;
        await Assert.That((string?)json["DisplayName"]).IsEqualTo("Hero");
        await Assert.That((string?)json["PrefabGuid"]).IsEqualTo("uid://abc123");
        await Assert.That((string?)json["Entities"]![0]!["Kind"]).IsEqualTo("Character");
        await Assert.That((string?)json["Materials"]![0]).IsEqualTo("materials/skin.json");
    }
}

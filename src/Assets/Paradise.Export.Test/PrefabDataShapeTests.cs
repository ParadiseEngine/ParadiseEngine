using Paradise.Export.Paths;

namespace Paradise.Export.Tests;

// Pins the prefab field mapping — the path rewrite an editor applies when it names the asset an
// object was placed from.
//
// The prefab TEMPLATE document went with schema v5: a template was a named bundle of
// LevelEntityData, and there is no such record any more. What survives is the path rule, which is
// about where an editor writes its side artifacts and never depended on the entity shape.
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
}

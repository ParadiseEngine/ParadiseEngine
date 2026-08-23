using Paradise.Export.Data;
using Paradise.Export.Paths;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>Schema v4 shape: <see cref="RenderableComponentData"/> carries the mesh GLB
/// reference AND the material slots that index against its primitives. Pins the serialized keys
/// and the mesh field path convention.</summary>
public class RenderableDataShapeTests
{
    [Test]
    public async Task renderable_serializes_mesh_and_mesh_node_keys()
    {
        var renderable = new RenderableComponentData { Mesh = "meshes/abc123.glb" };
        string json = ExportJsonWriter.SerializeToString(renderable);

        await Assert.That(json.Contains("\"Mesh\": \"meshes/abc123.glb\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"MeshNode\": null", StringComparison.Ordinal)).IsTrue();
        // Empty, not absent. A reader distinguishes "no overrides" from "key missing" only if the
        // key is always written, and every other list in this contract is written the same way.
        await Assert.That(json.Contains("\"Materials\": []", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task material_slots_round_trip_with_their_nulls_intact()
    {
        // A null slot is MEANINGFUL — it says the GLB's own embedded material wins for that
        // primitive — so it must survive the round trip as a null rather than being compacted
        // away or read back as "". Slot order is the contract; dropping one shifts every
        // override after it onto the wrong primitive.
        var level = new LevelData();
        level.Entities.Add(new LevelEntityData
        {
            Id = "Ball",
            Components =
            {
                LevelEntityExtensions.Entry(new RenderableComponentData
                {
                    Mesh = "meshes/abc123.glb",
                    Materials = ["materials/a.json", null, "materials/c.json"],
                }),
            },
        });

        // Through the whole path — writer, reader, generated registry — because the null is lost
        // in the generated reader, not in the serializer, if it is lost at all.
        RenderableComponentData round = ExportJsonReader
            .ReadLevel(ExportJsonWriter.SerializeToString(level))
            .Entities[0].Get<RenderableComponentData>()!;

        await Assert.That(round.Materials).IsEquivalentTo(
            new List<string?> { "materials/a.json", null, "materials/c.json" });
    }

    [Test]
    public async Task schema_version_is_four()
    {
        // Deliberate constant pin: the exported-data schema version is a cross-repo contract.
        // Changing it strands every committed document in every game repo until each is migrated
        // or re-exported, so it should be edited here first, on purpose.
#pragma warning disable TUnitAssertions0005
        await Assert.That(LevelData.CurrentSchemaVersion).IsEqualTo(4);
        // Equal, and deliberately so: v3 is refused rather than shimmed. See the doc on
        // MinimumSupportedVersion for why a mechanically-convertible break still gets no shim.
        await Assert.That(LevelData.MinimumSupportedVersion).IsEqualTo(4);
#pragma warning restore TUnitAssertions0005
        await Assert.That(new LevelData().SchemaVersion).IsEqualTo(4);
    }

    [Test]
    public async Task data_relative_mesh_field_maps_res_paths_under_the_data_dir()
    {
        // dataDir = <root>/data, so res:// (the project root) resolves its data/ child here.
        var paths = new ExportPaths("/tmp/paradise-root/data");

        await Assert.That(paths.DataRelativeMeshField("res://data/Models/knight.glb"))
            .IsEqualTo("Models/knight.glb");
        await Assert.That(paths.DataRelativeMeshField("res://data/Models/plants/plant_001.glb"))
            .IsEqualTo("Models/plants/plant_001.glb");
        await Assert.That(paths.DataRelativeMeshField("res://data/primitives/cube.glb"))
            .IsEqualTo("primitives/cube.glb");
    }

    [Test]
    public async Task data_relative_mesh_field_rejects_references_outside_the_data_dir()
    {
        var paths = new ExportPaths("/tmp/paradise-root/data");

        // res://Models (project root, not under data/) is unreachable by the runtime.
        await Assert.That(paths.DataRelativeMeshField("res://Models/knight.glb")).IsNull();
        await Assert.That(paths.DataRelativeMeshField("res://addons/foo.glb")).IsNull();
        await Assert.That(paths.DataRelativeMeshField("")).IsNull();
    }
}

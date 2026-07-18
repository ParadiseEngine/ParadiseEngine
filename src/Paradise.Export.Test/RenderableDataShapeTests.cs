using Paradise.Export.Data;
using Paradise.Export.Paths;
using Paradise.Export.Serialization;

namespace Paradise.Export.Tests;

/// <summary>Schema v2 shape: <see cref="RenderableComponentData"/> carries the mesh GLB
/// reference. Pins the serialized keys and the mesh field path convention.</summary>
public class RenderableDataShapeTests
{
    [Test]
    public async Task renderable_serializes_mesh_and_mesh_node_keys()
    {
        var renderable = new RenderableComponentData { Mesh = "meshes/abc123.glb" };
        string json = ExportJsonWriter.SerializeToString(renderable);

        await Assert.That(json.Contains("\"Mesh\": \"meshes/abc123.glb\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"MeshNode\": null", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task schema_version_is_two()
    {
        // Deliberate constant pin: the exported-data schema version is a cross-repo contract.
#pragma warning disable TUnitAssertions0005
        await Assert.That(LevelData.CurrentSchemaVersion).IsEqualTo(2);
#pragma warning restore TUnitAssertions0005
        await Assert.That(new LevelData().SchemaVersion).IsEqualTo(2);
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

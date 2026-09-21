using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Gltf.Test;

public class GltfSceneBakeTests
{
    private static GlbTestBuilder TriangleAsset(out int mesh)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        return b;
    }

    [Test]
    public async Task nested_trs_nodes_bake_to_the_numerics_composition()
    {
        var b = TriangleAsset(out var mesh);
        var child = b.AddNode(
            mesh: mesh,
            translation: [1f, 2f, 3f],
            rotation: [0f, 0.7071068f, 0f, 0.7071068f], // 90° about +Y
            scale: [2f, 2f, 2f],
            name: "child");
        var parent = b.AddNode(translation: [10f, 0f, 0f], children: [child]);
        b.SetSceneRoots(parent);

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Instances.Length).IsEqualTo(1);

        // Row-vector convention: local = S·R·T, world = local × parent. No handedness fixes.
        var childLocal =
            Matrix4x4.CreateScale(2f)
            * Matrix4x4.CreateFromQuaternion(new Quaternion(0f, 0.7071068f, 0f, 0.7071068f))
            * Matrix4x4.CreateTranslation(new Vector3(1f, 2f, 3f));
        var expected = childLocal * Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f));

        var world = asset.Instances[0].WorldTransform;
        await Assert.That(MatrixAlmostEqual(world, expected)).IsTrue();
    }

    [Test]
    public async Task matrix_node_loads_column_major_floats_verbatim()
    {
        var b = TriangleAsset(out var mesh);
        // glTF column-major with translation at flat [12,13,14] — byte-identical to the
        // System.Numerics row-major storage (translation at M41..M43).
        var node = b.AddNode(mesh: mesh, matrix:
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            5, 6, 7, 1,
        ]);
        b.SetSceneRoots(node);

        var asset = GltfSceneReader.Read(b.Build());
        var world = asset.Instances[0].WorldTransform;
        await Assert.That(world.Translation).IsEqualTo(new Vector3(5f, 6f, 7f));
    }

    [Test]
    public async Task nodes_without_meshes_are_skipped_but_their_children_visit()
    {
        var b = TriangleAsset(out var mesh);
        var leaf = b.AddNode(mesh: mesh, translation: [0f, 0f, -4f]);
        var empty = b.AddNode(translation: [1f, 0f, 0f], children: [leaf]);
        b.SetSceneRoots(empty);

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Instances.Length).IsEqualTo(1);
        await Assert.That(asset.Instances[0].WorldTransform.Translation).IsEqualTo(new Vector3(1f, 0f, -4f));
    }

    [Test]
    public async Task multiple_roots_and_shared_mesh_produce_one_instance_each()
    {
        var b = TriangleAsset(out var mesh);
        var a = b.AddNode(mesh: mesh, translation: [1f, 0f, 0f]);
        var c = b.AddNode(mesh: mesh, translation: [0f, 0f, 2f]);
        b.SetSceneRoots(a, c);

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Instances.Length).IsEqualTo(2);
        await Assert.That(asset.Meshes.Length).IsEqualTo(1); // shared geometry decoded once
    }

    [Test]
    public async Task explicit_default_scene_index_is_honored()
    {
        var b = TriangleAsset(out var mesh);
        var visible = b.AddNode(mesh: mesh, name: "in-scene-1");
        b.SetSceneRoots(); // scene 0: empty
        b.SetSceneIndex(1);
        var extraScene = new JsonArray { new JsonObject { ["nodes"] = new JsonArray { visible } } };

        var asset = GltfSceneReader.Read(b.Build(extraScenes: extraScene));
        await Assert.That(asset.Instances.Length).IsEqualTo(1);
        await Assert.That(asset.Instances[0].NodeName).IsEqualTo("in-scene-1");
    }

    [Test]
    public async Task deep_single_child_chain_loads_without_stack_overflow()
    {
        // A 100k-node single-child chain is a VALID tree — the iterative walk must load it
        // (the recursive implementation died with an uncatchable StackOverflowException).
        const int Depth = 100_000;
        var b = TriangleAsset(out var mesh);
        var current = b.AddNode(mesh: mesh, name: "leaf"); // deepest node carries the mesh
        for (var i = 1; i < Depth; i++)
        {
            current = b.AddNode(translation: [0f, 0f, 1f], children: [current]);
        }
        b.SetSceneRoots(current);

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Instances.Length).IsEqualTo(1);
        // Depth−1 parents each translate +1 on Z.
        await Assert.That(asset.Instances[0].WorldTransform.Translation.Z).IsEqualTo(Depth - 1f);
    }

    [Test]
    public async Task shared_child_between_two_parents_throws()
    {
        // glTF node graphs must be trees; a DAG (one child, two parents) exhausts the visit
        // budget and throws the typed error rather than silently double-instancing.
        var b = TriangleAsset(out var mesh);
        var child = b.AddNode(mesh: mesh);
        var p1 = b.AddNode(children: [child]);
        var p2 = b.AddNode(children: [child]);
        b.SetSceneRoots(p1, p2);
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task node_cycle_throws()
    {
        var b = TriangleAsset(out var mesh);
        // children arrays are declared before indices exist — build the cycle 0 → 1 → 0.
        var n0 = b.AddNode(mesh: mesh, children: [1]);
        _ = b.AddNode(children: [n0]);
        b.SetSceneRoots(n0);
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    private static bool MatrixAlmostEqual(Matrix4x4 a, Matrix4x4 b)
    {
        for (var r = 1; r <= 4; r++)
        {
            for (var c = 1; c <= 4; c++)
            {
                if (MathF.Abs(a[r - 1, c - 1] - b[r - 1, c - 1]) > 1e-5f) return false;
            }
        }
        return true;
    }
}

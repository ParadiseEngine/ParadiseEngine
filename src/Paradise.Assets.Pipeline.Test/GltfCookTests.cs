using System.Numerics;

using Paradise.Animation;
using Paradise.Assets.Gltf;
using Paradise.Assets.Gltf.Test;
using Paradise.Assets.Mesh;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A GLB cooks to blobs once, at extraction. Pinned here: rigid draws carry their node transform
/// in their vertices, skinned draws stay in bind space and name skin and node, draw order is
/// scene order (the material-slot contract), and the same GLB gives the same bytes.
/// </summary>
public class GltfCookTests
{
    [Test]
    public async Task a_rigid_mesh_bakes_its_node_transform_into_the_vertices()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        var node = b.AddNode(mesh: mesh, translation: [10f, 0f, 0f], name: "Crate");
        b.SetSceneRoots(node);

        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()));

        await Assert.That(cooked.Mesh.Layout).IsEqualTo(MeshVertexLayout.Static);
        await Assert.That(cooked.Mesh.VertexCount).IsEqualTo(3);
        await Assert.That(cooked.Mesh.Vertices[0]).IsEqualTo(10f);
        await Assert.That(cooked.Mesh.Vertices[MeshBlob.StaticFloatsPerVertex]).IsEqualTo(11f);
        await Assert.That(cooked.Mesh.BoundsMin).IsEqualTo(new Vector3(10, 0, 0));
        await Assert.That(cooked.Mesh.BoundsMax).IsEqualTo(new Vector3(11, 1, 0));
        await Assert.That(cooked.Mesh.Draws.Count).IsEqualTo(1);
        await Assert.That(cooked.Mesh.Draws[0]).IsEqualTo(new MeshDrawData(0, 3, 0, -1, -1, "Crate"));
        await Assert.That(cooked.Skeleton).IsNull();
        await Assert.That(cooked.Clips).IsEmpty();
    }

    [Test]
    public async Task a_zero_scale_axis_bakes_finite_normals_and_a_non_uniform_scale_keeps_them_on_the_surface()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var normal = b.AddFloatAccessor([0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], "VEC3");
        var tangent = b.AddFloatAccessor([1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, normal: normal, tangent: tangent));
        var flattened = b.AddNode(mesh: mesh, scale: [1f, 1f, 0f], name: "Flat");
        var squashed = b.AddNode(mesh: mesh, scale: [4f, 1f, 1f], name: "Squashed");
        b.SetSceneRoots(flattened, squashed);

        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()));

        var stride = MeshBlob.StaticFloatsPerVertex;
        await Assert.That(cooked.Mesh.Vertices.All(float.IsFinite)).IsTrue();
        // Squashed along X: the surface still faces +Z, and the tangent along X is unit length again.
        var second = 3 * stride;
        await Assert.That(cooked.Mesh.Vertices[second + 5]).IsEqualTo(1f);
        await Assert.That(cooked.Mesh.Vertices[second + 8]).IsEqualTo(1f);
    }

    [Test]
    public async Task two_instances_become_two_draws_in_scene_order_with_rebased_indices()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        var first = b.AddNode(mesh: mesh, name: "A");
        var second = b.AddNode(mesh: mesh, translation: [0f, 0f, 5f], name: "B");
        b.SetSceneRoots(first, second);

        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()));

        await Assert.That(cooked.Mesh.Draws.Select(d => d.Name ?? "")).IsEquivalentTo(new[] { "A", "B" });
        await Assert.That(cooked.Mesh.Draws[1].FirstIndex).IsEqualTo(3u);
        await Assert.That(cooked.Mesh.Draws[1].MaterialSlot).IsEqualTo(1);
        await Assert.That(cooked.Mesh.Indices).IsEquivalentTo(new uint[] { 0, 1, 2, 3, 4, 5 });
        await Assert.That(cooked.SlotMaterials).IsEquivalentTo(new[] { -1, -1 });
    }

    [Test]
    public async Task a_skinned_mesh_stays_in_bind_space_and_names_its_skin_and_node()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([0.75f, 0.25f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));
        var meshNode = b.AddNode(mesh: mesh, skin: 0, translation: [3f, 0f, 0f], name: "Body");
        var hip = b.AddNode(translation: [0f, 1f, 0f], name: "hip", children: [2]);
        var knee = b.AddNode(rotation: [0f, 0f, 0.7071068f, 0.7071068f], name: "knee");
        var ibm = b.AddFloatAccessor([.. Identity(), .. Identity()], "MAT4");
        b.AddSkin([hip, knee], ibm, name: "rig");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0.7071068f, 0.7071068f], "VEC4");
        b.AddAnimation("Walk", (knee, "rotation", times, values, null));
        b.SetSceneRoots(meshNode, hip);

        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()));

        await Assert.That(cooked.Mesh.Layout).IsEqualTo(MeshVertexLayout.Skinned);
        await Assert.That(cooked.Mesh.FloatsPerVertex).IsEqualTo(MeshBlob.SkinnedFloatsPerVertex);
        // Bind space: the node's translation is NOT baked; the palette carries it per frame.
        await Assert.That(cooked.Mesh.Vertices[0]).IsEqualTo(0f);
        await Assert.That(cooked.Mesh.Vertices[12]).IsEqualTo(0f);      // joint 0
        await Assert.That(cooked.Mesh.Vertices[13]).IsEqualTo(1f);      // joint 1
        await Assert.That(cooked.Mesh.Vertices[16]).IsEqualTo(0.75f);   // weight 0
        await Assert.That(cooked.Mesh.Draws[0].SkinIndex).IsEqualTo(0);
        await Assert.That(cooked.Mesh.Draws[0].NodeIndex).IsEqualTo(0);
        await Assert.That(cooked.Mesh.Skin!.Joints).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(cooked.Mesh.Skin.InverseBindMatrices.Length).IsEqualTo(2);
        await Assert.That(cooked.Skeleton).IsNotNull();
        await Assert.That(cooked.Skeleton!.JointCount).IsEqualTo(3);
        await Assert.That(cooked.Skeleton.Names.ToArray()).IsEquivalentTo(new[] { "Body", "hip", "knee" });
        await Assert.That(cooked.Skeleton.Parents[2]).IsEqualTo((short)1);
        await Assert.That(cooked.Skeleton.RestPoses[1].Translation).IsEqualTo(new Vector3(0, 1, 0));
        await Assert.That(cooked.Clips.Count).IsEqualTo(1);
        await Assert.That(cooked.Clips[0].Name).IsEqualTo("Walk");
        await Assert.That(cooked.Clips[0].Channels[0].Joint).IsEqualTo(2);
        await Assert.That(cooked.Clips[0].Channels[0].Path).IsEqualTo(ChannelPath.Rotation);
        await Assert.That(cooked.Clips[0].Duration).IsEqualTo(1f);
    }

    [Test]
    public async Task joints_are_depth_first_whatever_order_the_glb_lists_its_nodes()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([0.75f, 0.25f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));
        var knee = b.AddNode(rotation: [0f, 0f, 0.7071068f, 0.7071068f], name: "knee");                 // node 0, a child
        var hip = b.AddNode(translation: [0f, 1f, 0f], name: "hip", children: [knee]);                  // node 1, its parent
        var meshNode = b.AddNode(mesh: mesh, skin: 0, name: "Body");                                   // node 2
        var ibm = b.AddFloatAccessor([.. Identity(), .. Identity()], "MAT4");
        b.AddSkin([hip, knee], ibm, name: "rig");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0.7071068f, 0.7071068f], "VEC4");
        b.AddAnimation("Walk", (knee, "rotation", times, values, null));
        b.SetSceneRoots(meshNode, hip);

        var cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()));

        // Roots in node order, each followed by its subtree: hip, knee, Body.
        await Assert.That(cooked.Skeleton!.Names.ToArray()).IsEquivalentTo(new[] { "hip", "knee", "Body" });
        await Assert.That(cooked.Skeleton.Parents.ToArray()).IsEquivalentTo(new short[] { -1, 0, -1 });
        await Assert.That(cooked.Mesh.Skin!.Joints).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(cooked.Mesh.Draws[0].NodeIndex).IsEqualTo(2);
        await Assert.That(cooked.Clips[0].Channels[0].Joint).IsEqualTo(1);
        // The skeleton and clip cook to ozz archives that load back.
        await Assert.That(Skeleton.Load(cooked.Skeleton.Save()).FindJoint("knee")).IsEqualTo(1);
        await Assert.That(AnimationClip.Load(GltfCook.BuildClip(cooked.Clips[0], cooked.Skeleton).Save()).Name).IsEqualTo("Walk");
    }

    [Test]
    public async Task the_blobs_round_trip_and_the_same_glb_gives_the_same_bytes()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh, name: "Crate"));
        var glb = b.Build();

        var once = MeshBlobFormat.Write(GltfCook.Cook(GltfSceneReader.ReadGeometry(glb)).Mesh);
        var twice = MeshBlobFormat.Write(GltfCook.Cook(GltfSceneReader.ReadGeometry(glb)).Mesh);

        await Assert.That(once).IsEquivalentTo(twice);
        await Assert.That(MeshBlobFormat.Read(once).Draws[0].Name).IsEqualTo("Crate");
    }

    [Test]
    public async Task two_skins_in_one_glb_are_refused_with_the_reason()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        var node = b.AddNode(mesh: mesh);
        var ibm = b.AddFloatAccessor([.. Identity()], "MAT4");
        b.AddSkin([node], ibm);
        b.AddSkin([node], ibm);
        b.SetSceneRoots(node);

        var error = await Assert.That(() => GltfCook.Cook(GltfSceneReader.ReadGeometry(b.Build()))).Throws<InvalidDataException>();

        await Assert.That(error!.Message).Contains("one skeleton");
    }

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}

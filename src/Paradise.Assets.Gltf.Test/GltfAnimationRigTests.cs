using System.Numerics;

namespace Paradise.Assets.Gltf.Test;

/// <summary>The animation rig's math: rest-pose palettes are identity (skinned output ==
/// bind mesh), animated joints move only their weighted vertices, and channel sampling
/// follows glTF semantics (linear/step, clamp outside the clip).</summary>
public class GltfAnimationRigTests
{
    /// <summary>Two-joint chain along +Y with inverse binds matching the rest pose, and a
    /// 3-vertex primitive weighted (root, tip, half/half). Vertices sit at the joints.</summary>
    private static GltfAsset BuildRiggedAsset(out GltfAnimationData bendClip)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 0f], "VEC3");
        var jointsView = b.AddBufferView(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor(
            [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0.5f, 0.5f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));

        var meshNode = b.AddNode(mesh: mesh, skin: 0);                      // node 0
        var rootJoint = b.AddNode(name: "root", children: [2]);            // node 1 at origin
        var tipJoint = b.AddNode(name: "tip", translation: [0f, 1f, 0f]);  // node 2 at (0,1,0)

        // Inverse binds = inverse of the rest worlds: identity for root, T(0,-1,0) for tip.
        var ibm = b.AddFloatAccessor(
        [
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1,
        ], "MAT4");
        b.AddSkin([rootJoint, tipJoint], ibm);

        // "bend": rotate the ROOT joint 90° about Z over 1 s (0 → 90°).
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var rotations = b.AddFloatAccessor(
            [0f, 0f, 0f, 1f, 0f, 0f, 0.7071068f, 0.7071068f], "VEC4");
        b.AddAnimation("bend", (rootJoint, "rotation", times, rotations, null));

        b.SetSceneRoots(meshNode, rootJoint);
        var asset = GltfSceneReader.Read(b.Build());
        bendClip = asset.Animations[0];
        return asset;
    }

    [Test]
    public async Task rest_pose_skins_to_the_bind_mesh()
    {
        var asset = BuildRiggedAsset(out _);
        var rig = new GltfAnimationRig(asset);
        rig.EvaluatePose(null, 0f);
        var palette = new Matrix4x4[2];
        rig.ComputeJointPalette(0, asset.Instances[0].NodeIndex, palette);
        await Assert.That(palette[0]).IsEqualTo(Matrix4x4.Identity);
        await Assert.That(palette[1]).IsEqualTo(Matrix4x4.Identity);

        var primitive = asset.Meshes[0].Primitives[0];
        var output = new float[primitive.Vertices.Length];
        GltfAnimationRig.SkinVertices(primitive, palette, output);
        for (var i = 0; i < output.Length; i++)
        {
            await Assert.That(MathF.Abs(output[i] - primitive.Vertices[i]) < 1e-5f).IsTrue();
        }
    }

    [Test]
    public async Task bend_clip_end_rotates_weighted_vertices()
    {
        var asset = BuildRiggedAsset(out var bend);
        var rig = new GltfAnimationRig(asset);
        rig.EvaluatePose(bend, 1f); // full 90° about Z
        var palette = new Matrix4x4[2];
        rig.ComputeJointPalette(0, asset.Instances[0].NodeIndex, palette);

        var primitive = asset.Meshes[0].Primitives[0];
        var output = new float[primitive.Vertices.Length];
        GltfAnimationRig.SkinVertices(primitive, palette, output);

        // Vertex 0 (root-weighted, at the pivot) stays; vertex 1 (tip-weighted, at (0,1,0))
        // swings to (−1,0,0); vertex 2 (half/half at (0,0.5,0)) blends the two transforms.
        await Assert.That(MathF.Abs(output[0]) < 1e-5f && MathF.Abs(output[1]) < 1e-5f).IsTrue();
        var v1 = new Vector3(output[12], output[13], output[14]);
        await Assert.That((v1 - new Vector3(-1f, 0f, 0f)).Length() < 1e-4f).IsTrue();
        var v2 = new Vector3(output[24], output[25], output[26]);
        // Both joints rotate together (the tip inherits the root's rotation), so the midpoint
        // lands at (−0.5, 0, 0).
        await Assert.That((v2 - new Vector3(-0.5f, 0f, 0f)).Length() < 1e-4f).IsTrue();
    }

    [Test]
    public async Task sampling_interpolates_and_clamps()
    {
        var asset = BuildRiggedAsset(out var bend);
        var rig = new GltfAnimationRig(asset);
        var palette = new Matrix4x4[2];
        var primitive = asset.Meshes[0].Primitives[0];
        var output = new float[primitive.Vertices.Length];

        // Halfway: 45° — tip vertex at (−sin45, cos45, 0).
        rig.EvaluatePose(bend, 0.5f);
        rig.ComputeJointPalette(0, asset.Instances[0].NodeIndex, palette);
        GltfAnimationRig.SkinVertices(primitive, palette, output);
        var tip = new Vector3(output[12], output[13], output[14]);
        await Assert.That((tip - new Vector3(-0.7071f, 0.7071f, 0f)).Length() < 1e-3f).IsTrue();

        // Past the end: clamps to the last key.
        rig.EvaluatePose(bend, 5f);
        rig.ComputeJointPalette(0, asset.Instances[0].NodeIndex, palette);
        GltfAnimationRig.SkinVertices(primitive, palette, output);
        var clamped = new Vector3(output[12], output[13], output[14]);
        await Assert.That((clamped - new Vector3(-1f, 0f, 0f)).Length() < 1e-4f).IsTrue();
    }

    [Test]
    public async Task find_animation_is_case_insensitive()
    {
        var asset = BuildRiggedAsset(out _);
        var rig = new GltfAnimationRig(asset);
        await Assert.That(rig.FindAnimation("BEND")).IsNotNull();
        await Assert.That(rig.FindAnimation("missing")).IsNull();
    }
}

using System.Numerics;

namespace Paradise.Assets.Gltf.Test;

/// <summary>Skins + animations through the reader: JOINTS_0/WEIGHTS_0 interleaving (raw u8/u16
/// indices as floats), inverse bind matrices (transpose-dual load), the node hierarchy's rest
/// TRS, and keyframe channel extraction (LINEAR/STEP kept, CUBICSPLINE rejected).</summary>
public class GltfSkinAnimationTests
{
    private static GlbTestBuilder TwoJointSkinnedTriangle(out int meshNode)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        // JOINTS_0: raw (non-normalized) u8 quads.
        var jointsView = b.AddBufferView(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 });
        var joints = b.AddAccessor(jointsView, GlbTestBuilder.UByte, "VEC4", 3);
        var weights = b.AddFloatAccessor([0.75f, 0.25f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], "VEC4");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, joints: joints, weights: weights));

        // Node 0 = mesh (skinned), nodes 1..2 = joint chain.
        var jointChild = 0; // patched after adding
        meshNode = b.AddNode(mesh: mesh, skin: 0);
        var root = b.AddNode(translation: [0f, 1f, 0f], name: "hip", children: [2]);
        jointChild = b.AddNode(rotation: [0f, 0f, 0.7071068f, 0.7071068f], name: "knee");
        _ = jointChild;

        // Inverse bind matrices: identity + a translation, loaded transpose-dual.
        var ibm = b.AddFloatAccessor(
        [
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1,
        ], "MAT4");
        b.AddSkin([root, jointChild], ibm);
        b.SetSceneRoots(meshNode, root);
        return b;
    }

    [Test]
    public async Task skinned_primitive_interleaves_joints_and_weights()
    {
        var b = TwoJointSkinnedTriangle(out _);
        var asset = GltfSceneReader.Read(b.Build());

        var primitive = asset.Meshes[0].Primitives[0];
        await Assert.That(primitive.JointsWeights).IsNotNull();
        var jw = primitive.JointsWeights!;
        await Assert.That(jw.Length).IsEqualTo(3 * GltfPrimitive.SkinFloatsPerVertex);
        // vertex 0: joints (0,1,0,0), weights (0.75, 0.25, 0, 0)
        await Assert.That(jw[0]).IsEqualTo(0f);
        await Assert.That(jw[1]).IsEqualTo(1f);
        await Assert.That(jw[4]).IsEqualTo(0.75f);
        await Assert.That(jw[5]).IsEqualTo(0.25f);
        // instance links mesh → skin 0 and its node index
        await Assert.That(asset.Instances[0].SkinIndex).IsEqualTo(0);
        await Assert.That(asset.Instances[0].NodeIndex).IsEqualTo(0);
    }

    [Test]
    public async Task rigid_primitive_has_no_skin_stream()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        var node = b.AddNode(mesh: mesh);
        b.SetSceneRoots(node);
        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Meshes[0].Primitives[0].JointsWeights).IsNull();
        await Assert.That(asset.Instances[0].SkinIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task skin_loads_joints_and_inverse_bind_matrices()
    {
        var b = TwoJointSkinnedTriangle(out _);
        var asset = GltfSceneReader.Read(b.Build());

        await Assert.That(asset.Skins.Length).IsEqualTo(1);
        var skin = asset.Skins[0];
        await Assert.That(skin.JointNodes.Length).IsEqualTo(2);
        await Assert.That(skin.InverseBindMatrices[0]).IsEqualTo(Matrix4x4.Identity);
        // Second IBM carries translation (0,-1,0) — glTF column-major loads transpose-dual into
        // the row-vector convention, landing in M42.
        await Assert.That(skin.InverseBindMatrices[1].M42).IsEqualTo(-1f);
        // Node hierarchy: rest transforms + parents.
        await Assert.That(asset.Nodes[1].RestTranslation).IsEqualTo(new Vector3(0f, 1f, 0f));
        await Assert.That(asset.Nodes[2].ParentIndex).IsEqualTo(1);
        await Assert.That(asset.Nodes[0].ParentIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task animation_channels_extract_times_values_and_paths()
    {
        var b = TwoJointSkinnedTriangle(out _);
        var times = b.AddFloatAccessor([0f, 0.5f, 1f], "SCALAR");
        var rotations = b.AddFloatAccessor(
        [
            0f, 0f, 0f, 1f,
            0f, 0f, 0.7071068f, 0.7071068f,
            0f, 0f, 1f, 0f,
        ], "VEC4");
        var translations = b.AddFloatAccessor([0f, 0f, 0f, 0f, 2f, 0f, 0f, 4f, 0f], "VEC3");
        b.AddAnimation("walk",
            (1, "rotation", times, rotations, null),
            (1, "translation", times, translations, "STEP"));

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Animations.Length).IsEqualTo(1);
        var anim = asset.Animations[0];
        await Assert.That(anim.Name).IsEqualTo("walk");
        await Assert.That(anim.Channels.Length).IsEqualTo(2);
        await Assert.That(anim.Duration).IsEqualTo(1f);

        var rotation = anim.Channels[0];
        await Assert.That(rotation.Path).IsEqualTo(GltfAnimationPath.Rotation);
        await Assert.That(rotation.Step).IsFalse();
        await Assert.That(rotation.Times.Length).IsEqualTo(3);
        await Assert.That(rotation.Values.Length).IsEqualTo(12);

        var translation = anim.Channels[1];
        await Assert.That(translation.Path).IsEqualTo(GltfAnimationPath.Translation);
        await Assert.That(translation.Step).IsTrue();
        await Assert.That(translation.Values[4]).IsEqualTo(2f);
    }

    [Test]
    public async Task cubicspline_interpolation_is_rejected()
    {
        var b = TwoJointSkinnedTriangle(out _);
        var times = b.AddFloatAccessor([0f], "SCALAR");
        // CUBICSPLINE output = 3 values per key (in-tangent, value, out-tangent).
        var values = b.AddFloatAccessor(new float[12], "VEC4");
        b.AddAnimation("bad", (1, "rotation", times, values, "CUBICSPLINE"));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }
}

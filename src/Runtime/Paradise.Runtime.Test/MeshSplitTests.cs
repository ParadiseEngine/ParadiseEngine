using System.Numerics;
using Paradise.Animation;
using Paradise.Assets.Mesh;
using Paradise.Runtime.Pbr;

namespace Paradise.Runtime.Test;

/// <summary>How a cooked blob becomes per-draw upload streams, and why the asset stages palettes in model
/// space. Pure CPU: runs on every lane.</summary>
public class MeshSplitTests
{
    /// <summary>A skinned blob as the cook writes one with a rigid prop beside the skinned body: every
    /// vertex is 20 floats. The 12 static floats of vertex v are 100·v + f. Skinned vertices name joint slot
    /// 0 or 1 at full weight, and the rigid draw's carry slot 0 at zero weight, as the cook writes them.</summary>
    internal static MeshData MixedBlob()
    {
        const int stride = MeshBlob.SkinnedFloatsPerVertex;
        var vertices = new float[6 * stride];
        for (var v = 0; v < 6; v++)
        {
            for (var f = 0; f < MeshBlob.StaticFloatsPerVertex; f++)
                vertices[v * stride + f] = 100 * v + f;
            vertices[v * stride + 12] = v < 3 ? v % 2 : 0f;      // first joint slot
            vertices[v * stride + 16] = v < 3 ? 1f : 0f;          // its weight
        }

        return new MeshData(
            MeshVertexLayout.Skinned, vertices,
            // Skinned draw over vertices 0-2; rigid draw over 3-5, indexed out of order and repeating one.
            [0, 1, 2, 5, 3, 4, 3, 5, 4],
            [new MeshDrawData(0, 3, 0, NodeIndex: 2, SkinIndex: 0, "body"), new MeshDrawData(3, 6, 1, NodeIndex: -1, SkinIndex: -1, "prop")],
            Vector3.Zero, Vector3.One,
            new MeshSkinData([0, 1], [Matrix4x4.Identity, Matrix4x4.Identity], "rig.skeleton"));
    }

    [Test]
    public async Task each_draw_gets_its_own_vertices_in_the_layout_it_renders_with()
    {
        // Through the blob format, as the asset type decodes it: the fixture must be a blob the format accepts.
        var cooked = PbrMeshAssetType.Split("/mixed.mesh", MeshBlobFormat.Read(MeshBlobFormat.Write(MixedBlob())));

        var body = cooked.Draws[0];
        await Assert.That(body.Skinned).IsTrue();
        await Assert.That(body.MeshJoint).IsEqualTo(2);
        await Assert.That(body.Vertices.Length).IsEqualTo(3 * MeshBlob.SkinnedFloatsPerVertex);
        await Assert.That(body.Indices).IsEquivalentTo(new uint[] { 0, 1, 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(body.Vertices[20 + 12]).IsEqualTo(1f); // vertex 1's joint slot survives the cut
        await Assert.That(body.Vertices[20 + 16]).IsEqualTo(1f);

        // The rigid draw in a skinned blob is cut to the 12-float static layout a rigid primitive renders
        // with; uploaded at 20 floats it would be read 48 bytes at a time and scramble.
        var prop = cooked.Draws[1];
        await Assert.That(prop.Skinned).IsFalse();
        await Assert.That(prop.MeshJoint).IsEqualTo(-1);
        await Assert.That(prop.Vertices.Length).IsEqualTo(3 * MeshBlob.StaticFloatsPerVertex);
        // First use order: source 5, 3, 4 become 0, 1, 2.
        await Assert.That(prop.Indices).IsEquivalentTo(new uint[] { 0, 1, 2, 1, 0, 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(prop.Vertices[0]).IsEqualTo(500f);
        await Assert.That(prop.Vertices[11]).IsEqualTo(511f);
        await Assert.That(prop.Vertices[12]).IsEqualTo(300f);
    }

    [Test]
    public async Task a_draw_reading_outside_the_blob_is_invalid_data()
    {
        var blob = MixedBlob();
        var badIndex = blob with { Indices = [0, 1, 9, 5, 3, 4, 3, 5, 4] };
        var badRange = blob with { Draws = [new MeshDrawData(6, 9, 0, -1, -1, null)] };

        await Assert.That(() => PbrMeshAssetType.Split("/bad.mesh", badIndex)).Throws<InvalidDataException>();
        await Assert.That(() => PbrMeshAssetType.Split("/bad.mesh", badRange)).Throws<InvalidDataException>();
    }

    /// <summary>The asset stages without a mesh-joint term. That places a skinned vertex where staging with
    /// the draw's joint does under a placement that includes the joint's model matrix: the model space the
    /// blob's rigid draws are baked into.</summary>
    [Test]
    public async Task model_space_staging_places_skinned_vertices_like_mesh_joint_staging()
    {
        Matrix4x4[] models =
        [
            Matrix4x4.CreateTranslation(0, 1, 0),
            Matrix4x4.CreateRotationZ(0.7f) * Matrix4x4.CreateTranslation(1, 2, 0),
            Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationY(1.1f) * Matrix4x4.CreateTranslation(-2, 0, 3),
        ];
        int[] joints = [0, 1];
        Matrix4x4[] inverseBinds = [Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateRotationX(-0.4f)];
        var root = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(10, 0, -4);
        const int meshJoint = 2;

        var modelSpace = new Matrix4x4[2];
        var jointSpace = new Matrix4x4[2];
        SkinningPalette.Compute(models, joints, inverseBinds, -1, modelSpace);
        SkinningPalette.Compute(models, joints, inverseBinds, meshJoint, jointSpace);

        var vertex = new Vector3(0.3f, -0.2f, 0.9f);
        for (var i = 0; i < joints.Length; i++)
        {
            var viaModel = Vector3.Transform(vertex, modelSpace[i] * root);
            var viaJoint = Vector3.Transform(vertex, jointSpace[i] * (models[meshJoint] * root));
            await Assert.That(Vector3.Distance(viaModel, viaJoint)).IsLessThan(1e-4f);
        }
    }
}

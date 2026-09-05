using System.Numerics;

namespace Paradise.Assets.Mesh.Test;

/// <summary>The blob is what the runtime pins: every field round-trips, a foreign or truncated blob is refused by name, and the same data gives the same bytes.</summary>
public class MeshBlobTests
{
    private static MeshData Sample(MeshVertexLayout layout = MeshVertexLayout.Static)
    {
        var skinned = layout == MeshVertexLayout.Skinned;
        var floats = skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;
        var vertices = Enumerable.Range(0, 3 * floats).Select(i => i * 0.5f).ToArray();
        if (skinned)
        {
            // Joint slots name the skin's two palette entries, not the running sequence.
            for (var v = 0; v < 3; v++) for (var j = 0; j < 4; j++) vertices[v * floats + MeshBlob.StaticFloatsPerVertex + j] = j % 2;
        }

        return new MeshData(
            layout, vertices, [0, 1, 2],
            [new MeshDrawData(0, 3, 0, skinned ? 4 : -1, skinned ? 0 : -1, "Crate")],
            new Vector3(-1, -2, -3), new Vector3(1, 2, 3),
            skinned ? new MeshSkinData([3, 7], [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], "models/rig.skeleton") : null);
    }

    [Test]
    public async Task a_static_mesh_round_trips()
    {
        var bytes = MeshBlobFormat.Write(Sample());

        var read = MeshBlobFormat.Read(bytes);

        await Assert.That(read.Layout).IsEqualTo(MeshVertexLayout.Static);
        await Assert.That(read.Vertices).IsEquivalentTo(Sample().Vertices);
        await Assert.That(read.Indices).IsEquivalentTo(new uint[] { 0, 1, 2 });
        await Assert.That(read.Draws).IsEquivalentTo(Sample().Draws);
        await Assert.That(read.BoundsMin).IsEqualTo(new Vector3(-1, -2, -3));
        await Assert.That(read.BoundsMax).IsEqualTo(new Vector3(1, 2, 3));
    }

    [Test]
    public async Task a_skinned_mesh_keeps_its_wider_stride_and_its_skin_binding()
    {
        var read = MeshBlobFormat.Read(MeshBlobFormat.Write(Sample(MeshVertexLayout.Skinned)));

        await Assert.That(read.FloatsPerVertex).IsEqualTo(MeshBlob.SkinnedFloatsPerVertex);
        await Assert.That(read.VertexCount).IsEqualTo(3);
        await Assert.That(read.Draws[0].SkinIndex).IsEqualTo(0);
        await Assert.That(read.Draws[0].NodeIndex).IsEqualTo(4);
        await Assert.That(read.Skin!.Joints).IsEquivalentTo(new[] { 3, 7 });
        await Assert.That(read.Skin.InverseBindMatrices[1]).IsEqualTo(Matrix4x4.CreateTranslation(0, -1, 0));
        await Assert.That(read.Skin.Skeleton).IsEqualTo("models/rig.skeleton");
        await Assert.That(MeshBlobFormat.Read(MeshBlobFormat.Write(Sample())).Skin).IsNull();
    }

    [Test]
    public async Task a_skinned_draw_without_a_skin_and_a_vertex_past_the_palette_are_refused()
    {
        var noSkin = Sample(MeshVertexLayout.Skinned) with { Skin = new MeshSkinData([], [], "models/rig.skeleton") };
        var pastPalette = Sample(MeshVertexLayout.Skinned);
        pastPalette.Vertices[MeshBlob.StaticFloatsPerVertex] = 2f;
        var mismatched = Sample(MeshVertexLayout.Skinned) with { Skin = new MeshSkinData([3, 7], [Matrix4x4.Identity], "models/rig.skeleton") };
        // A skinned mesh is bound to one skeleton and the blob says which; a skin that names none is
        // a mesh the runtime could not pose, refused before it is written.
        var unbound = Sample(MeshVertexLayout.Skinned) with { Skin = new MeshSkinData([3, 7], [Matrix4x4.Identity, Matrix4x4.Identity]) };
        await Assert.That(() => MeshBlobFormat.Write(unbound)).Throws<ArgumentException>();
        await Assert.That(() => MeshBlobFormat.Write(Sample(MeshVertexLayout.Skinned) with { Skin = null })).Throws<ArgumentException>();

        var missing = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(noSkin))).Throws<InvalidDataException>();
        await Assert.That(missing!.Message).Contains("no skin");
        var past = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(pastPalette))).Throws<InvalidDataException>();
        await Assert.That(past!.Message).Contains("palette slot 2 of 2");
        await Assert.That(() => MeshBlobFormat.Write(mismatched)).Throws<ArgumentException>();

        // A host indexes its slot table by the draw's slot; the format promises it is never negative.
        var negativeSlot = Sample() with { Draws = [Sample().Draws[0] with { MaterialSlot = -1 }] };
        var slot = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(negativeSlot))).Throws<InvalidDataException>();
        await Assert.That(slot!.Message).Contains("material slot -1");

        var notANumber = Sample(MeshVertexLayout.Skinned);
        notANumber.Vertices[MeshBlob.SkinnedFloatsPerVertex + MeshBlob.StaticFloatsPerVertex + 1] = float.NaN;
        var fractional = Sample(MeshVertexLayout.Skinned);
        fractional.Vertices[2 * MeshBlob.SkinnedFloatsPerVertex + MeshBlob.StaticFloatsPerVertex + 3] = 0.5f;
        var nan = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(notANumber))).Throws<InvalidDataException>();
        await Assert.That(nan!.Message).Contains("Vertex 1");
        var half = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(fractional))).Throws<InvalidDataException>();
        await Assert.That(half!.Message).Contains("Vertex 2 names palette slot 0.5");
    }

    [Test]
    public async Task the_pinned_root_is_readable_without_a_copy()
    {
        using var reference = MeshBlobFormat.Open(MeshBlobFormat.Write(Sample()));

        var name = reference.Value.Draws[0].Name.ToString();
        var floats = reference.Value.Vertices.Length;
        var skinned = reference.Value.IsSkinned;

        await Assert.That(name).IsEqualTo("Crate");
        await Assert.That(floats).IsEqualTo(3 * MeshBlob.StaticFloatsPerVertex);
        await Assert.That(skinned).IsFalse();
    }

    [Test]
    public async Task the_same_data_gives_the_same_bytes()
    {
        await Assert.That(MeshBlobFormat.Write(Sample())).IsEquivalentTo(MeshBlobFormat.Write(Sample()));
    }

    [Test]
    public async Task foreign_bytes_and_a_newer_version_are_refused_by_name()
    {
        var bytes = MeshBlobFormat.Write(Sample());
        var newer = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(newer.AsSpan(4), MeshBlob.ExpectedVersion + 1);

        await Assert.That(MeshBlobFormat.IsMeshBlob(bytes)).IsTrue();
        await Assert.That(MeshBlobFormat.IsMeshBlob("\u0001ozz-skeleton"u8.ToArray())).IsFalse();
        var foreign = await Assert.That(() => MeshBlobFormat.Read("not a blob at all"u8.ToArray())).Throws<InvalidDataException>();
        await Assert.That(foreign!.Message).Contains("magic");
        var version = await Assert.That(() => MeshBlobFormat.Read(newer)).Throws<InvalidDataException>();
        await Assert.That(version!.Message).Contains("version");
    }

    [Test]
    public async Task a_draw_past_the_index_buffer_is_refused()
    {
        var broken = Sample() with { Draws = [new MeshDrawData(1, 3, 0, -1, -1, null)] };

        var error = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(broken))).Throws<InvalidDataException>();

        await Assert.That(error!.Message).Contains("index buffer");
    }

    [Test]
    public async Task an_index_past_the_vertex_count_is_refused()
    {
        var broken = Sample() with { Indices = [0, 1, 3] };

        var error = await Assert.That(() => MeshBlobFormat.Read(MeshBlobFormat.Write(broken))).Throws<InvalidDataException>();

        await Assert.That(error!.Message).Contains("vertex 3 of 3");
    }

    [Test]
    public async Task a_vertex_stream_that_is_not_whole_vertices_is_refused_at_write()
    {
        var broken = Sample() with { Vertices = new float[13] };

        await Assert.That(() => MeshBlobFormat.Write(broken)).Throws<ArgumentException>();
    }
}

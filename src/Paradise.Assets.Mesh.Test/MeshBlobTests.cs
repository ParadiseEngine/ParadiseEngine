using System.Numerics;

namespace Paradise.Assets.Mesh.Test;

/// <summary>The blob is what the runtime pins: every field round-trips, a foreign or truncated blob is refused by name, and the same data gives the same bytes.</summary>
public class MeshBlobTests
{
    private static MeshData Sample(MeshVertexLayout layout = MeshVertexLayout.Static)
    {
        var floats = layout == MeshVertexLayout.Skinned ? MeshBlob.SkinnedFloatsPerVertex : MeshBlob.StaticFloatsPerVertex;
        var vertices = Enumerable.Range(0, 3 * floats).Select(i => i * 0.5f).ToArray();
        return new MeshData(
            layout, vertices, [0, 1, 2],
            [new MeshDrawData(0, 3, 0, layout == MeshVertexLayout.Skinned ? 4 : -1, layout == MeshVertexLayout.Skinned ? 0 : -1, "Crate")],
            new Vector3(-1, -2, -3), new Vector3(1, 2, 3));
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
        await Assert.That(MeshBlobFormat.IsMeshBlob("PSKL"u8.ToArray())).IsFalse();
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
    public async Task a_vertex_stream_that_is_not_whole_vertices_is_refused_at_write()
    {
        var broken = Sample() with { Vertices = new float[13] };

        await Assert.That(() => MeshBlobFormat.Write(broken)).Throws<ArgumentException>();
    }
}

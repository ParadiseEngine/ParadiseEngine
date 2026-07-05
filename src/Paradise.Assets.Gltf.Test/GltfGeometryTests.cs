using System.IO;

namespace Paradise.Assets.Gltf.Test;

public class GltfGeometryTests
{
    [Test]
    public async Task full_quad_round_trips_all_attributes()
    {
        var glb = GlbTestBuilder.FullQuad(out var meshIndex).Build();
        var asset = GltfSceneReader.Read(glb);

        await Assert.That(asset.Meshes.Length).IsEqualTo(1);
        await Assert.That(asset.Instances.Length).IsEqualTo(1);
        await Assert.That(asset.Instances[0].MeshIndex).IsEqualTo(meshIndex);
        await Assert.That(asset.Instances[0].NodeName).IsEqualTo("quad");

        var primitive = asset.Meshes[0].Primitives[0];
        await Assert.That(primitive.VertexCount).IsEqualTo(4);
        await Assert.That(primitive.HasNormals).IsTrue();
        await Assert.That(primitive.HasTexCoords).IsTrue();
        await Assert.That(primitive.HasTangents).IsTrue();
        await Assert.That(primitive.Indices).IsEquivalentTo(new uint[] { 0, 1, 2, 0, 2, 3 });

        // Vertex 2 = (1,1,0), normal +Z, uv (1,1), tangent (1,0,0,1) — interleaved at 12 floats.
        var v2 = primitive.Vertices.AsSpan(2 * GltfPrimitive.FloatsPerVertex, GltfPrimitive.FloatsPerVertex).ToArray();
        await Assert.That(v2).IsEquivalentTo(new float[] { 1, 1, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1 });
    }

    [Test]
    [Arguments(GlbTestBuilder.UByte)]
    [Arguments(GlbTestBuilder.UShort)]
    [Arguments(GlbTestBuilder.UInt)]
    public async Task index_component_types_widen_to_uint(int componentType)
    {
        var glb = GlbTestBuilder.FullQuad(out _, componentType).Build();
        var asset = GltfSceneReader.Read(glb);
        await Assert.That(asset.Meshes[0].Primitives[0].Indices).IsEquivalentTo(new uint[] { 0, 1, 2, 0, 2, 3 });
    }

    [Test]
    public async Task non_indexed_primitive_gets_sequential_indices()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Meshes[0].Primitives[0].Indices).IsEquivalentTo(new uint[] { 0, 1, 2 });
    }

    [Test]
    public async Task missing_optional_attributes_are_default_filled_and_flagged()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var primitive = GltfSceneReader.Read(b.Build()).Meshes[0].Primitives[0];
        await Assert.That(primitive.HasNormals).IsFalse();
        await Assert.That(primitive.HasTexCoords).IsFalse();
        await Assert.That(primitive.HasTangents).IsFalse();

        var v0 = primitive.Vertices.AsSpan(0, GltfPrimitive.FloatsPerVertex).ToArray();
        // pos (0,0,0), default normal +Y, uv zero, default tangent (1,0,0,+1).
        await Assert.That(v0).IsEquivalentTo(new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1 });
    }

    [Test]
    public async Task interleaved_buffer_view_honors_byte_stride()
    {
        // One shared bufferView holding pos3+uv2 per vertex (20-byte stride), two accessors
        // windowing it at different offsets.
        var b = new GlbTestBuilder();
        float[] interleaved =
        [
            0, 0, 0, /*uv*/ 0.25f, 0.75f,
            1, 0, 0, /*uv*/ 0.5f, 0.5f,
            0, 1, 0, /*uv*/ 1f, 0.125f,
        ];
        var view = b.AddBufferView(interleaved, byteStride: 20);
        var position = b.AddAccessor(view, GlbTestBuilder.Float, "VEC3", 3);
        var uv = b.AddAccessor(view, GlbTestBuilder.Float, "VEC2", 3, byteOffset: 12);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, uv: uv));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var primitive = GltfSceneReader.Read(b.Build()).Meshes[0].Primitives[0];
        await Assert.That(primitive.Vertices[1 * GltfPrimitive.FloatsPerVertex + 0]).IsEqualTo(1f);
        await Assert.That(primitive.Vertices[1 * GltfPrimitive.FloatsPerVertex + 6]).IsEqualTo(0.5f);
        await Assert.That(primitive.Vertices[2 * GltfPrimitive.FloatsPerVertex + 7]).IsEqualTo(0.125f);
    }

    [Test]
    public async Task normalized_ushort_texcoords_convert_to_float()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        // (0, 65535) → (0, 1); (32767, 16383) ≈ (0.49999, 0.24999).
        byte[] uvBytes = [0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0x3F, 0, 0, 0, 0];
        var uvView = b.AddBufferView(uvBytes);
        var uv = b.AddAccessor(uvView, GlbTestBuilder.UShort, "VEC2", 3, normalized: true);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, uv: uv));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var primitive = GltfSceneReader.Read(b.Build()).Meshes[0].Primitives[0];
        await Assert.That(primitive.Vertices[6]).IsEqualTo(0f);
        await Assert.That(primitive.Vertices[7]).IsEqualTo(1f);
        await Assert.That(MathF.Abs(primitive.Vertices[GltfPrimitive.FloatsPerVertex + 6] - 32767f / 65535f) < 1e-6f).IsTrue();
    }

    [Test]
    public async Task non_triangle_mode_throws()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0], "VEC3");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, mode: 1)); // LINES
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task sparse_accessor_throws()
    {
        var b = new GlbTestBuilder();
        var view = b.AddBufferView(new float[] { 0, 0, 0 });
        var position = b.AddAccessor(view, GlbTestBuilder.Float, "VEC3", 1, sparse: true);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task external_buffer_uri_throws()
    {
        var b = GlbTestBuilder.FullQuad(out _);
        b.UseExternalBufferUri("mesh.bin");
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task huge_declared_count_fails_the_range_check_before_any_allocation()
    {
        // The blocking review finding on this PR: accessor.count is untrusted JSON metadata —
        // a few-hundred-byte GLB declaring count=200000000 must fail the (allocation-free)
        // range check, never trigger a multi-GB attribute allocation. If validation ordering
        // regresses, this test fails by OOM/timeout instead of the typed throw.
        var b = new GlbTestBuilder();
        var view = b.AddBufferView(new float[] { 0, 0, 0, 1, 0, 0 }); // 24 bytes
        var position = b.AddAccessor(view, GlbTestBuilder.Float, "VEC3", count: 200_000_000);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task overflowing_count_times_stride_still_fails_the_range_check()
    {
        // count × stride wraps int (2_000_000 × 2_000 ≈ 4e9); the long-arithmetic range check
        // must throw the typed error, not spuriously pass into an opaque slice failure.
        var b = new GlbTestBuilder();
        var view = b.AddBufferView(new float[] { 0, 0, 0 }, byteStride: 2_000);
        var position = b.AddAccessor(view, GlbTestBuilder.Float, "VEC3", count: 2_000_000);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task accessor_count_exceeding_buffer_view_throws()
    {
        var b = new GlbTestBuilder();
        var view = b.AddBufferView(new float[] { 0, 0, 0, 1, 0, 0 }); // 2 VEC3s
        var position = b.AddAccessor(view, GlbTestBuilder.Float, "VEC3", 5); // claims 5
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task index_exceeding_vertex_count_throws()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        var indices = b.AddIndexAccessor([0, 1, 7]); // 7 > 2
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, indices: indices));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task attribute_count_mismatch_throws()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3"); // 3 verts
        var normal = b.AddFloatAccessor([0, 0, 1, 0, 0, 1], "VEC3");            // 2 normals
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, normal));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }
}

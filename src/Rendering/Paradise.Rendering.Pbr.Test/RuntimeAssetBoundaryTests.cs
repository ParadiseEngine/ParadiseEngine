using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Assets.Mesh;

namespace Paradise.Rendering.Pbr.Test;

public class RuntimeAssetBoundaryTests
{
    [Test]
    public async Task renderer_has_no_source_container_dependency_or_bulk_import_api()
    {
        var assembly = typeof(PbrRenderer).Assembly;
        await Assert.That(assembly.GetReferencedAssemblies().Any(name => name.Name == "Paradise.Assets.Gltf")).IsFalse();
        await Assert.That(typeof(PbrRenderer).GetMethods().Any(method => method.Name == "UploadMesh")).IsFalse();
        await Assert.That(typeof(PbrMaterialDesc).GetProperties().Any(property => property.Name.EndsWith("Image", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task cooked_mesh_spans_upload_with_material_slots_and_survive_source_disposal(bool skinned)
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var baseline = backend.ResourceCount;
        var red = pbr.Materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1));
        var green = pbr.Materials.AddDefaultMaterial(new Vector4(0, 1, 0, 1));
        // Ownership is explicit even for a loaded material which no draw currently references.
        var unused = pbr.Materials.AddDefaultMaterial(new Vector4(0, 0, 1, 1));
        var materialIds = new[] { red, green, unused };
        var data = CookedData(skinned);
        var primitives = UploadCookedDraws(pbr, MeshBlobFormat.Write(data), materialIds);

        await Assert.That(primitives.Length).IsEqualTo(2);
        await Assert.That(primitives[0].MaterialId).IsEqualTo(green);
        await Assert.That(primitives[1].MaterialId).IsEqualTo(red);
        for (var i = 0; i < primitives.Length; i++)
        {
            var primitive = primitives[i];
            var draw = data.Draws[i];
            await Assert.That(primitive.Skinned).IsEqualTo(skinned);
            await Assert.That(primitive.IndexCount).IsEqualTo(draw.IndexCount);
            await Assert.That(backend.BufferData[primitive.VertexBuffer].AsSpan()
                .SequenceEqual(MemoryMarshal.AsBytes(data.Vertices.AsSpan()))).IsTrue();
            await Assert.That(backend.BufferData[primitive.IndexBuffer].AsSpan()
                .SequenceEqual(MemoryMarshal.AsBytes(data.Indices.AsSpan((int)draw.FirstIndex, (int)draw.IndexCount)))).IsTrue();
        }

        foreach (var primitive in primitives) await Assert.That(pbr.ReleasePrimitive(primitive)).IsTrue();
        foreach (var materialId in materialIds) await Assert.That(pbr.Materials.ReleaseMaterial(materialId)).IsTrue();
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
    }

    [Test]
    public async Task cooked_upload_transaction_rolls_back_geometry_without_releasing_borrowed_materials()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var data = CookedData(false);
        // A non-triangle draw fits the blob's index bounds but is rejected by geometry upload.
        var broken = data with { Draws = [data.Draws[0], data.Draws[1] with { IndexCount = 2 }] };
        var bytes = MeshBlobFormat.Write(broken);
        var baseline = backend.ResourceCount;

        await Assert.That(() => UploadCookedDraws(pbr, bytes, [material, material])).Throws<ArgumentException>();
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
        await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(1);
        var retry = UploadCookedDraws(pbr, MeshBlobFormat.Write(data), [material, material]);
        foreach (var primitive in retry) pbr.ReleasePrimitive(primitive);
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
    }

    [Test]
    public async Task interleaved_and_separate_skin_stream_uploads_produce_identical_geometry()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var (vertices, indices) = Procedural.UnitCube();
        var weights = new float[vertices.Length / 12 * 8];
        for (var i = 0; i < weights.Length; i += 8) weights[i + 4] = 1f;
        var separate = pbr.UploadSkinnedPrimitive(vertices.AsSpan(), weights.AsSpan(), indices.AsSpan(), material);
        var cooked = CookedData(true);
        var interleaved = pbr.UploadSkinnedPrimitive(cooked.Vertices.AsSpan(), cooked.Indices.AsSpan(), material);

        await Assert.That(backend.BufferData[separate.VertexBuffer].AsSpan()
            .SequenceEqual(backend.BufferData[interleaved.VertexBuffer])).IsTrue();
        await Assert.That(separate.Skinned && interleaved.Skinned).IsTrue();
        pbr.ReleasePrimitive(separate);
        pbr.ReleasePrimitive(interleaved);
    }

    [Test]
    public async Task default_material_description_preserves_neutral_uniforms()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var material = pbr.Materials.AddMaterial(new PbrMaterialDesc());
        var group = backend.BindGroups[pbr.Materials.GetBindGroup(material)];
        var buffer = group.Entries.ToArray()[0].Buffer;
        var uniforms = MemoryMarshal.Read<MaterialUniformsGpu>(backend.BufferData[buffer]);

        await Assert.That(uniforms.BaseColorFactor).IsEqualTo(Vector4.One);
        await Assert.That(uniforms.UvOffsetScale).IsEqualTo(new Vector4(0, 0, 1, 1));
        await Assert.That(uniforms.NormalScale).IsEqualTo(1f);
        await Assert.That(uniforms.OcclusionStrength).IsEqualTo(1f);
        await Assert.That(uniforms.ProcParams).IsEqualTo(new Vector4(0, 1, 1, 1));
        await Assert.That(pbr.Materials.TextureCount).IsEqualTo(0);
    }

    // A loader owns the transaction and material dependency map; PBR sees only geometry spans.
    private static PbrPrimitive[] UploadCookedDraws(PbrRenderer pbr, byte[] bytes, int[] materialIds)
    {
        using var reference = MeshBlobFormat.Open(bytes);
        ref var blob = ref reference.Value;
        var vertices = blob.Vertices.ToSpan();
        var indices = blob.Indices.ToSpan();
        var result = new List<PbrPrimitive>();
        try
        {
            for (var i = 0; i < blob.Draws.Length; i++)
            {
                ref var draw = ref blob.Draws[i];
                var slice = indices.Slice((int)draw.FirstIndex, (int)draw.IndexCount);
                var material = materialIds[draw.MaterialSlot];
                result.Add(draw.IsSkinned
                    ? pbr.UploadSkinnedPrimitive(vertices, slice, material)
                    : pbr.UploadPrimitive(vertices, slice, material, stride: blob.FloatsPerVertex));
            }
            return result.ToArray();
        }
        catch
        {
            foreach (var primitive in result) pbr.ReleasePrimitive(primitive);
            throw;
        }
    }

    private static MeshData CookedData(bool skinned)
    {
        var (vertices, indices) = Procedural.UnitCube();
        if (skinned)
        {
            var interleaved = new float[vertices.Length / 12 * 20];
            for (var i = 0; i < vertices.Length / 12; i++)
            {
                vertices.AsSpan(i * 12, 12).CopyTo(interleaved.AsSpan(i * 20));
                interleaved[i * 20 + 16] = 1f;
            }
            vertices = interleaved;
        }
        var half = (uint)indices.Length / 2;
        return new MeshData(skinned ? MeshVertexLayout.Skinned : MeshVertexLayout.Static, vertices, indices,
            [new MeshDrawData(0, half, 1, skinned ? 0 : -1, skinned ? 0 : -1, "first"),
             new MeshDrawData(half, half, 0, skinned ? 0 : -1, skinned ? 0 : -1, "second")],
            new Vector3(-0.5f), new Vector3(0.5f),
            skinned ? new MeshSkinData([0], [Matrix4x4.Identity], "actor.skeleton") : null);
    }
}

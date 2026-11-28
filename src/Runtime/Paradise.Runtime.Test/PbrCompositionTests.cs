using System.Numerics;
using Paradise.Features;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Runtime.Pbr;
using Zio;
using Zio.FileSystems;

namespace Paradise.Runtime.Test;

/// <summary>The PBR half of the composition layer, against a recording backend.</summary>
[NotInParallel]
public class PbrCompositionTests
{
    private static readonly AssetLoaderOptions s_inline = new() { Background = static work => work() };

    [Test]
    public async Task the_scene_builder_keeps_one_instance_per_key_and_forgets_absent_keys()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var cube = CubeMesh.Data();
        var mesh = new PbrMesh([renderer.UploadPrimitive(cube.Vertices, cube.Indices, renderer.Materials.AddDefaultMaterial(Vector4.One))]);
        var other = new PbrMesh([.. mesh.Primitives]);
        var builder = new PbrSceneBuilder();

        builder.Begin();
        var first = builder.Add(1, mesh, Matrix4x4.Identity);
        builder.Add(2, mesh, Matrix4x4.CreateTranslation(1, 0, 0));
        builder.End();
        await Assert.That(() => builder.Add(1, mesh, Matrix4x4.Identity)).Throws<InvalidOperationException>();

        builder.Begin();
        var again = builder.Add(1, mesh, Matrix4x4.CreateTranslation(0, 2, 0), jointOffset: 7);
        builder.End();
        await Assert.That(ReferenceEquals(first, again)).IsTrue();
        await Assert.That(again.Model.M42).IsEqualTo(2f);
        await Assert.That(again.JointOffset).IsEqualTo(7);
        await Assert.That(builder.Scene.Instances.Count).IsEqualTo(1);
        await Assert.That(builder.Count).IsEqualTo(1);

        builder.Begin();
        var swapped = builder.Add(1, other, Matrix4x4.Identity);
        builder.End();
        await Assert.That(ReferenceEquals(first, swapped)).IsFalse();
    }

    [Test]
    public async Task a_dropped_key_s_instance_sits_out_a_frame_then_serves_a_new_key_as_new()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var mesh = Cube(renderer);
        var builder = new PbrSceneBuilder();

        builder.Begin();
        var first = builder.Add(1, mesh, Matrix4x4.CreateTranslation(1, 2, 3), jointOffset: 4);
        first.Highlight = 1f;
        first.ReceivesDecals = false;
        first.GiMode = PbrGiMode.Dynamic;
        first.ShadowsOnly = true;
        first.GiMesh = mesh;
        builder.End();

        // Key 1 is gone this frame; its instance was drawn last frame and must not move to key 2 yet.
        builder.Begin();
        var second = builder.Add(2, mesh, Matrix4x4.Identity);
        builder.End();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();

        builder.Begin();
        builder.Add(2, mesh, Matrix4x4.Identity);
        var third = builder.Add(3, mesh, Matrix4x4.Identity);
        builder.End();
        await Assert.That(ReferenceEquals(first, third)).IsTrue();
        await Assert.That(Settings(third)).IsEquivalentTo(Settings(new PbrInstance { Mesh = mesh }));
    }

    [Test]
    public async Task an_instance_replaced_by_a_mesh_swap_is_not_reused_in_the_same_frame()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var mesh = Cube(renderer);
        var other = new PbrMesh([.. mesh.Primitives]);
        var builder = new PbrSceneBuilder();

        builder.Begin();
        var first = builder.Add(1, mesh, Matrix4x4.Identity);
        builder.End();

        builder.Begin();
        builder.Add(1, other, Matrix4x4.Identity);
        var newcomer = builder.Add(2, mesh, Matrix4x4.Identity);
        builder.End();
        await Assert.That(ReferenceEquals(first, newcomer)).IsFalse();

        builder.Begin();
        builder.Add(1, other, Matrix4x4.Identity);
        builder.Add(2, mesh, Matrix4x4.Identity);
        var later = builder.Add(3, mesh, Matrix4x4.Identity);
        builder.End();
        await Assert.That(ReferenceEquals(first, later)).IsTrue();
    }

    [Test]
    public async Task steady_spawn_churn_allocates_nothing()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var mesh = Cube(renderer);
        var builder = new PbrSceneBuilder();

        Churn(builder, mesh, frames: 50, startFrame: 0);
        var allocated = Churn(builder, mesh, frames: 200, startFrame: 50);

        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(builder.Count).IsEqualTo(64);
    }

    /// <summary>64 live keys, 8 replaced by new keys every frame; the bytes the frames allocated.</summary>
    private static long Churn(PbrSceneBuilder builder, PbrMesh mesh, int frames, int startFrame)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = startFrame; frame < startFrame + frames; frame++)
        {
            builder.Begin();
            for (var key = frame * 8L; key < frame * 8L + 64; key++)
                builder.Add(key, mesh, Matrix4x4.Identity).GiMode = PbrGiMode.Dynamic;
            builder.End();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static PbrMesh Cube(PbrRenderer renderer)
    {
        var cube = CubeMesh.Data();
        return new PbrMesh([renderer.UploadPrimitive(cube.Vertices, cube.Indices, renderer.Materials.AddDefaultMaterial(Vector4.One))]);
    }

    /// <summary>Every public setting of an instance, so a field added to <see cref="PbrInstance"/> and
    /// forgotten by the builder's reset fails here.</summary>
    private static List<string> Settings(PbrInstance instance)
    {
        var type = typeof(PbrInstance);
        var fields = type.GetFields().Select(field => $"{field.Name}={field.GetValue(instance)}");
        var properties = type.GetProperties().Where(property => property.CanWrite)
            .Select(property => $"{property.Name}={property.GetValue(instance)}");
        return [.. fields, .. properties];
    }

    [Test]
    public async Task a_skinned_blob_uploads_its_rigid_draws_in_the_static_layout()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var content = new MemoryFileSystem();
        content.WriteAllBytes("/mixed.mesh", Paradise.Assets.Mesh.MeshBlobFormat.Write(MeshSplitTests.MixedBlob()));
        using var loader = new AssetLoader(content, s_inline);

        var handle = loader.Load("/mixed.mesh", new PbrMeshAssetType(renderer));
        loader.Update();

        await Assert.That(handle.Error).IsNull();
        await Assert.That(handle.TryGet(out var asset)).IsTrue();
        var body = asset!.Mesh.Primitives[0];
        var prop = asset.Mesh.Primitives[1];
        await Assert.That(body.Skinned).IsTrue();
        await Assert.That(body.VertexByteLength).IsEqualTo(3ul * 20 * sizeof(float));
        await Assert.That(prop.Skinned).IsFalse();
        await Assert.That(prop.VertexByteLength).IsEqualTo(3ul * 12 * sizeof(float));
        await Assert.That(prop.IndexCount).IsEqualTo(6u);
        await Assert.That(asset.MeshJoints).IsEquivalentTo(new[] { 2, -1 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        loader.Release(handle);
        loader.Update();
    }

    [Test]
    public async Task a_mesh_still_loading_is_skipped_until_it_is_ready()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var content = new MemoryFileSystem();
        content.WriteAllBytes("/cube.mesh", CubeMesh.Cooked());
        var queued = new List<Action>();
        using var loader = new AssetLoader(content, new AssetLoaderOptions { Background = queued.Add });
        var type = new PbrMeshAssetType(renderer);
        var handle = loader.Load("/cube.mesh", type);
        var builder = new PbrSceneBuilder();

        builder.Begin();
        await Assert.That(builder.Add(1, handle, Matrix4x4.Identity)).IsNull();
        await Assert.That(builder.Skipped).IsEqualTo(1);
        builder.End();

        foreach (var work in queued) work();
        loader.Update();
        builder.Begin();
        await Assert.That(builder.Add(1, handle, Matrix4x4.Identity)).IsNotNull();
        await Assert.That(handle.Value!.Mesh.Primitives.Length).IsEqualTo(1);
        builder.End();

        var buffers = backend.Buffers.Count;
        loader.Release(handle);
        loader.Update();
        // Releasing the mesh released its vertex and index buffers.
        await Assert.That(backend.Buffers.Count).IsEqualTo(buffers - 2);
    }

    [Test]
    public async Task the_stager_hands_out_palette_offsets_end_to_end()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var stager = new JointPaletteStager(renderer);
        Matrix4x4[] models = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1, 0), Matrix4x4.CreateTranslation(0, 2, 0)];
        Matrix4x4[] inverseBinds = [Matrix4x4.Identity, Matrix4x4.Identity];

        stager.Begin();
        var first = stager.Stage(models, [0, 1], inverseBinds);
        var second = stager.Stage(models, [1, 2], inverseBinds);
        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(2);
        await Assert.That(stager.Staged).IsEqualTo(4);
        await Assert.That(() => stager.Stage(models, [5], [Matrix4x4.Identity])).Throws<ArgumentException>();

        stager.Begin();
        await Assert.That(stager.Stage(models, [2], [Matrix4x4.Identity])).IsEqualTo(0);
    }

    [Test]
    public async Task materials_take_upload_ready_textures_and_share_them_by_content()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var pixel = new CompressedTextureData([255, 0, 0, 255], 1, 1, TextureFormat.Rgba8UnormSrgb, 1, 1, 4,
            [new CompressedTextureMipLevel(1, 1, 0, 4, 4, 1, 1, 1)]);
        var textures = new PbrMaterialTextures { BaseColor = pixel };
        var before = backend.CreatedTextures.Count;

        var first = renderer.Materials.AddMaterial(new PbrMaterialDesc(), textures);
        var second = renderer.Materials.AddMaterial(new PbrMaterialDesc(), textures with { BaseColor = pixel with { } });

        await Assert.That(backend.CreatedTextures.Count).IsEqualTo(before + 1);
        await Assert.That(backend.CreatedTextures[^1].Format).IsEqualTo(TextureFormat.Rgba8UnormSrgb);
        await Assert.That(renderer.Materials.ReleaseMaterial(first)).IsTrue();
        await Assert.That(renderer.Materials.ReleaseMaterial(second)).IsTrue();
        await Assert.That(backend.Textures.Contains(backend.CreatedTextures[^1].Texture)).IsFalse();
    }

    [Test]
    public async Task transcoding_slots_without_payloads_keeps_them_empty()
    {
        var transcoded = new PbrKtx2Textures().Transcode(TextureCompressionFormats.None);
        await Assert.That(transcoded).IsEqualTo(default(PbrMaterialTextures));
    }

    [Test]
    public async Task the_material_type_transcodes_in_decode_and_adds_in_upload()
    {
        var backend = new RecordingBackend();
        using var renderer = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        using var content = new MemoryFileSystem();
        content.WriteAllText("/red.material", "red");
        var parsed = 0;
        var type = new PbrMaterialAssetType(renderer, backend, (_, bytes) =>
        {
            parsed++;
            return new PbrMaterialSource(new PbrMaterialDesc { BaseColorFactor = new Vector4(1, 0, 0, 1) }, default);
        });
        using var loader = new AssetLoader(content, s_inline);

        var handle = loader.Load("/red.material", type);
        await Assert.That(parsed).IsEqualTo(1);
        loader.Update();

        await Assert.That(handle.TryGet(out var material)).IsTrue();
        await Assert.That(renderer.Materials.IsBlend(material!.MaterialId)).IsFalse();
        loader.Release(handle);
        loader.Update();
        await Assert.That(() => renderer.Materials.IsBlend(material.MaterialId)).Throws<ArgumentException>();
    }
}

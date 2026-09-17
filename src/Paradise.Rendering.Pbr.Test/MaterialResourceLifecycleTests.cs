using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class MaterialResourceLifecycleTests
{
    private static GltfMaterialData Material() => new("material", Vector4.One,
        0f, 0.8f, Vector3.Zero, 1f, 1f, 0f, GltfAlphaMode.Opaque, 0.5f, false,
        -1, -1, -1, -1, -1, GltfUvTransform.Identity);

    private static GltfImageData[] Images() =>
        [new(File.ReadAllBytes(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Paradise.Assets.Textures.Test", "fixtures", "color-srgb-etc1s.ktx2"))))];

    private static GltfMaterialData Textured() => Material() with
    {
        BaseColorImage = 0, MetallicRoughnessImage = 0, OcclusionImage = 0, EmissiveImage = 0,
    };

    private static int AddTextured(MaterialResourceCache materials, GltfMaterialData material, GltfImageData[] images)
    {
        try { return materials.AddMaterial(material, images); }
        catch (DllNotFoundException error)
        {
            Skip.Test($"libktx not loadable on this host: {error.Message}");
            throw;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task uploaded_mesh_exposes_all_materials_for_unload_including_unused_textures_and_fallback(bool needsFallback)
    {
        var renderer = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(renderer, new FeatureSwitches(), 16, 16);
        var survivingMaterial = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var survivingGroup = pbr.Materials.GetBindGroup(survivingMaterial);
        var resourceCount = renderer.ResourceCount;
        var textureCount = renderer.Textures.Count;
        var (vertices, indices) = Procedural.UnitCube();
        var primitive = new GltfPrimitive(vertices, indices, 1, true, true, true);
        var sourceMaterials = new[] { Textured(), Material() with { BaseColorFactor = new Vector4(1, 0, 0, 1) } };
        var asset = new GltfAsset([], [new GltfMeshData("mesh", needsFallback
            ? [primitive, primitive with { MaterialIndex = -1 }]
            : [primitive])], sourceMaterials, Images(), [], [], []);
        PbrMesh[] meshes;
        int[] materialIds;
        try { meshes = pbr.UploadMesh(asset, out materialIds); }
        catch (DllNotFoundException error)
        {
            Skip.Test($"libktx not loadable on this host: {error.Message}");
            throw;
        }

        await Assert.That(materialIds.Length).IsEqualTo(sourceMaterials.Length + (needsFallback ? 1 : 0));
        for (var i = 0; i < sourceMaterials.Length; i++)
            await Assert.That(pbr.Materials.GetTraceSurface(materialIds[i]).BaseColor).IsEqualTo(sourceMaterials[i].BaseColorFactor);
        await Assert.That(meshes[0].Primitives[0].MaterialId).IsEqualTo(materialIds[1]);
        if (needsFallback)
            await Assert.That(meshes[0].Primitives[1].MaterialId).IsEqualTo(materialIds[^1]);
        await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(1 + materialIds.Length);
        await Assert.That(pbr.Materials.TextureCount).IsEqualTo(2);
        await Assert.That(renderer.Textures.Count).IsEqualTo(textureCount + 2);

        foreach (var mesh in meshes)
            foreach (var uploaded in mesh.Primitives)
                await Assert.That(pbr.ReleasePrimitive(uploaded)).IsTrue();
        foreach (var materialId in materialIds)
            await Assert.That(pbr.Materials.ReleaseMaterial(materialId)).IsTrue();

        await Assert.That(pbr.Materials.MaterialCount).IsEqualTo(1);
        await Assert.That(pbr.Materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Count).IsEqualTo(textureCount);
        await Assert.That(renderer.ResourceCount).IsEqualTo(resourceCount);
        await Assert.That(pbr.Materials.GetBindGroup(survivingMaterial)).IsEqualTo(survivingGroup);
        await Assert.That(renderer.BindGroups.ContainsKey(survivingGroup)).IsTrue();
    }

    [Test]
    public async Task releasing_materials_during_a_frame_is_rejected_without_retiring_resources()
    {
        var renderer = new ResourceTrackingRenderer();
        var rendering = false;
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"))
        {
            IsFrameInProgress = () => rendering,
        };
        var id = materials.AddDefaultMaterial(Vector4.One);
        var group = materials.GetBindGroup(id);
        rendering = true;
        await Assert.That(() => materials.ReleaseMaterial(id)).Throws<InvalidOperationException>();
        await Assert.That(materials.GetBindGroup(id)).IsEqualTo(group);
        await Assert.That(renderer.BindGroups.ContainsKey(group)).IsTrue();
        rendering = false;
        await Assert.That(materials.ReleaseMaterial(id)).IsTrue();
    }

    [Test]
    public async Task shared_textures_live_until_the_last_material_and_released_ids_never_alias()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var images = Images();
        var first = AddTextured(materials, Textured(), images);
        var second = AddTextured(materials, Textured(), images);
        var firstGroup = materials.GetBindGroup(first);
        var secondGroup = materials.GetBindGroup(second);
        var firstBuffer = renderer.BindGroups[firstGroup].Entries.ToArray()[0].Buffer;
        await Assert.That(materials.TextureCount).IsEqualTo(2);
        await Assert.That(renderer.Textures.Count).IsEqualTo(4);

        await Assert.That(materials.ReleaseMaterial(first)).IsTrue();
        await Assert.That(materials.ReleaseMaterial(first)).IsFalse();
        await Assert.That(materials.ReleaseMaterial(-1)).IsFalse();
        await Assert.That(materials.ReleaseMaterial(int.MaxValue)).IsFalse();
        await Assert.That(renderer.DestroyedBuffers[firstBuffer]).IsEqualTo(1);
        await Assert.That(renderer.DestroyedBindGroups[firstGroup]).IsEqualTo(1);
        await Assert.That(materials.GetBindGroup(second)).IsEqualTo(secondGroup);
        await Assert.That(renderer.Textures.Count).IsEqualTo(4);
        await Assert.That(materials.MaterialCount).IsEqualTo(1);

        await Assert.That(materials.ReleaseMaterial(second)).IsTrue();
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Count).IsEqualTo(2);
        var replacement = materials.AddDefaultMaterial(new Vector4(1, 0, 0, 1));
        await Assert.That(replacement).IsGreaterThan(second);
        await Assert.That(materials.MaterialCount).IsEqualTo(1);
        await Assert.That(() => materials.GetBindGroup(first)).Throws<ArgumentException>();
        await Assert.That(() => materials.GetProgramId(first)).Throws<ArgumentException>();
        await Assert.That(() => materials.IsBlend(first)).Throws<ArgumentException>();
        await Assert.That(() => materials.GetTraceSurface(first)).Throws<ArgumentException>();
        await Assert.That(() => { _ = materials.TargetsOf(first).Length; }).Throws<ArgumentException>();
        await Assert.That(() => materials.UpdateExtraEntry(first, default)).Throws<ArgumentException>();

        materials.Dispose();
        materials.Dispose();
        await Assert.That(materials.ReleaseMaterial(replacement)).IsFalse();
        await Assert.That(renderer.ResourceCount).IsEqualTo(0);
        await Assert.That(() => materials.GetBindGroup(replacement)).Throws<ObjectDisposedException>();
        await Assert.That(() => materials.AddDefaultMaterial(Vector4.One)).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task failed_material_creation_releases_new_resources_and_preserves_shared_owners(int failure)
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var images = Images();
        var survivor = AddTextured(materials, Material() with { BaseColorImage = 0 }, images);
        var group = materials.GetBindGroup(survivor);
        var before = renderer.ResourceCount;
        var material = Textured();
        if (failure == 0) material = material with { EmissiveImage = 99 };
        if (failure == 1) renderer.FailNextBindGroup = true;
        if (failure == 2) renderer.FailTextureWriteAfter = 0;
        if (failure == 0)
            await Assert.That(() => materials.AddMaterial(material, images)).Throws<ArgumentException>();
        else
            await Assert.That(() => materials.AddMaterial(material, images)).Throws<InvalidOperationException>();
        await Assert.That(renderer.ResourceCount).IsEqualTo(before);
        await Assert.That(materials.MaterialCount).IsEqualTo(1);
        await Assert.That(materials.TextureCount).IsEqualTo(1);
        await Assert.That(materials.GetBindGroup(survivor)).IsEqualTo(group);

        var retry = materials.AddMaterial(Textured(), images);
        materials.ReleaseMaterial(survivor);
        await Assert.That(materials.TextureCount).IsEqualTo(2);
        materials.ReleaseMaterial(retry);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task failed_default_texture_upload_releases_partial_constructor_resources(int successfulWrites)
    {
        var renderer = new ResourceTrackingRenderer { FailTextureWriteAfter = successfulWrites };
        await Assert.That(() => new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr")))
            .Throws<InvalidOperationException>();
        await Assert.That(renderer.ResourceCount).IsEqualTo(0);
    }

    [Test]
    public async Task malformed_images_borrow_defaults_without_destroying_them_on_release()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var defaults = renderer.Textures.Keys.ToArray();
        var material = Textured() with { NormalImage = 0 };
        var first = materials.AddMaterial(material, [new GltfImageData([1, 2, 3])]);
        var second = materials.AddMaterial(material, [new GltfImageData([1, 2, 3])]);
        materials.ReleaseMaterial(first);
        materials.ReleaseMaterial(second);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Keys.ToHashSet().SetEquals(defaults)).IsTrue();
        await Assert.That(renderer.DestroyedTextures.Count).IsEqualTo(0);
        materials.Dispose();
        await Assert.That(renderer.ResourceCount).IsEqualTo(0);
    }

    [Test]
    public async Task extra_entry_replacement_is_transactional_and_keeps_external_resources_borrowed()
    {
        var renderer = new ResourceTrackingRenderer();
        using var programs = new MaterialPrograms(renderer);
        using var materials = new MaterialResourceCache(renderer, programs.BuiltIn);
        var program = programs.Register(materials, ShaderProgramLoader.Load(
            typeof(MaterialResourceLifecycleTests).Assembly, "Shaders.instancedMaterialFixture"), "vertexMain", "fragmentMain",
            new MaterialProgramOptions
            {
                InstancedVertexEntryPoint = "vertexMainInstanced",
                InstancedFragmentEntryPoint = "fragmentMainInstanced",
            });
        var first = renderer.CreateBuffer(new BufferDesc("first", 16, BufferUsage.Uniform));
        var second = renderer.CreateBuffer(new BufferDesc("second", 16, BufferUsage.Uniform));
        var id = materials.AddMaterial(Material(), [], program, [BindGroupEntryDesc.ForBuffer(7, first, 0, 16)]);
        var original = materials.GetBindGroup(id);
        renderer.FailNextBindGroup = true;
        await Assert.That(() => materials.UpdateExtraEntry(id, BindGroupEntryDesc.ForBuffer(7, second, 0, 16)))
            .Throws<InvalidOperationException>();
        await Assert.That(materials.GetBindGroup(id)).IsEqualTo(original);
        await Assert.That(renderer.BindGroups[original].Entries.ToArray()[7].Buffer).IsEqualTo(first);

        materials.UpdateExtraEntry(id, BindGroupEntryDesc.ForBuffer(7, second, 0, 16));
        var updated = materials.GetBindGroup(id);
        await Assert.That(renderer.BindGroups.ContainsKey(original)).IsFalse();
        await Assert.That(renderer.BindGroups[updated].Entries.ToArray()[7].Buffer).IsEqualTo(second);
        materials.ReleaseMaterial(id);
        await Assert.That(renderer.Buffers.ContainsKey(first)).IsTrue();
        await Assert.That(renderer.Buffers.ContainsKey(second)).IsTrue();
        renderer.DestroyBuffer(first);
        renderer.DestroyBuffer(second);
    }

    [Test]
    public async Task failed_target_rebind_retries_without_losing_the_previous_group()
    {
        var renderer = new ResourceTrackingRenderer();
        using var targets = new GraphTextureRegistry(renderer);
        using var programs = new MaterialPrograms(renderer);
        using var materials = new MaterialResourceCache(renderer, programs.BuiltIn, 16, targets);
        var program = programs.Register(materials, ShaderProgramLoader.Load(
            typeof(MaterialResourceLifecycleTests).Assembly, "Shaders.refractionFixture"), "vertexMain", "fragmentMain");
        var target = new TextureDesc(null, 4, 4, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.RenderAttachment);
        targets.Ensure("capture", target);
        var id = materials.AddMaterial(Material(), [], program, [], [new MaterialTarget(7, "capture")]);
        var original = materials.GetBindGroup(id);
        targets.Ensure("capture", target with { Width = 8 });
        renderer.FailNextBindGroup = true;
        await Assert.That(() => materials.ResolveTargets()).Throws<InvalidOperationException>();
        await Assert.That(materials.GetBindGroup(id)).IsEqualTo(original);
        await Assert.That(renderer.BindGroups.ContainsKey(original)).IsTrue();

        materials.ResolveTargets();
        var updated = materials.GetBindGroup(id);
        await Assert.That(renderer.BindGroups[updated].Entries.ToArray()[7].View).IsEqualTo(targets.View("capture"));
        await Assert.That(renderer.BindGroups.ContainsKey(original)).IsFalse();
        materials.ReleaseMaterial(id);
        targets.Ensure("capture", target);
        materials.ResolveTargets();
        await Assert.That(renderer.BindGroups.Count).IsEqualTo(0);
    }

    [Test]
    public async Task releasing_program_from_a_cache_without_its_layout_preserves_the_program_and_pipelines()
    {
        var renderer = new ResourceTrackingRenderer();
        using var programs = new MaterialPrograms(renderer);
        using var materials = new MaterialResourceCache(renderer, programs.BuiltIn);
        using var otherMaterials = new MaterialResourceCache(renderer, programs.BuiltIn);
        var shader = ShaderProgramLoader.Load(typeof(MaterialResourceLifecycleTests).Assembly, "Shaders.instancedMaterialFixture");
        var program = programs.Register(materials, shader, "vertexMain", "fragmentMain", new MaterialProgramOptions
        {
            InstancedVertexEntryPoint = "vertexMainInstanced",
            InstancedFragmentEntryPoint = "fragmentMainInstanced",
        });
        var ordinary = programs.Get(program, BlendMode.Opaque);
        var instanced = programs.GetInstancedPipeline(program, false, BlendMode.Opaque);

        await Assert.That(() => programs.Release(otherMaterials, program))
            .Throws<InvalidOperationException>().WithMessageContaining("no registered layout");
        await Assert.That(programs.CustomProgramCount).IsEqualTo(1);
        await Assert.That(programs.Get(program, BlendMode.Opaque)).IsEqualTo(ordinary);
        await Assert.That(programs.GetInstancedPipeline(program, false, BlendMode.Opaque)).IsEqualTo(instanced);
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(2);
        await Assert.That(renderer.DestroyedPipelines.Count).IsEqualTo(0);

        await Assert.That(programs.Release(materials, program)).IsTrue();
        await Assert.That(programs.CustomProgramCount).IsEqualTo(0);
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(0);
        await Assert.That(renderer.DestroyedPipelines[ordinary]).IsEqualTo(1);
        await Assert.That(renderer.DestroyedPipelines[instanced]).IsEqualTo(1);
    }

    [Test]
    public async Task releasing_custom_programs_checks_material_dependencies_and_releases_every_pipeline_variant()
    {
        var renderer = new ResourceTrackingRenderer();
        using var programs = new MaterialPrograms(renderer);
        using var materials = new MaterialResourceCache(renderer, programs.BuiltIn);
        var shader = ShaderProgramLoader.Load(typeof(MaterialResourceLifecycleTests).Assembly, "Shaders.instancedMaterialFixture");
        var options = new MaterialProgramOptions
        {
            InstancedVertexEntryPoint = "vertexMainInstanced",
            InstancedFragmentEntryPoint = "fragmentMainInstanced",
        };
        var program = programs.Register(materials, shader, "vertexMain", "fragmentMain", options);
        var external = renderer.CreateBuffer(new BufferDesc("external", 16, BufferUsage.Uniform));
        var id = materials.AddMaterial(Material(), [], program, [BindGroupEntryDesc.ForBuffer(7, external, 0, 16)]);
        var builtin = programs.Get(0, BlendMode.Opaque);
        var custom = programs.Get(program, BlendMode.Opaque);
        programs.Get(program, BlendMode.AlphaBlend);
        programs.GetInstancedPipeline(program, false, BlendMode.Opaque);
        programs.GetInstancedPipeline(program, false, BlendMode.AlphaBlend);
        await Assert.That(() => programs.Release(materials, program)).Throws<InvalidOperationException>();
        await Assert.That(programs.Get(program, BlendMode.Opaque)).IsEqualTo(custom);
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(5);

        materials.ReleaseMaterial(id);
        await Assert.That(programs.Release(materials, program)).IsTrue();
        await Assert.That(programs.Release(materials, program)).IsFalse();
        await Assert.That(programs.Release(materials, 0)).IsFalse();
        await Assert.That(programs.Release(materials, -1)).IsFalse();
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(1);
        await Assert.That(renderer.Pipelines.ContainsKey(builtin)).IsTrue();
        await Assert.That(renderer.DestroyedPipelines.Count).IsEqualTo(4);
        await Assert.That(() => programs.Get(program, BlendMode.Opaque)).Throws<ArgumentException>();
        await Assert.That(() => programs.GetInstancedPipeline(program, false, BlendMode.Opaque)).Throws<ArgumentException>();
        await Assert.That(() => materials.AddMaterial(Material(), [], program,
            [BindGroupEntryDesc.ForBuffer(7, external, 0, 16)])).Throws<ArgumentException>();

        var replacement = programs.Register(materials, shader, "vertexMain", "fragmentMain", options);
        await Assert.That(replacement).IsGreaterThan(program);
        await Assert.That(programs.CustomProgramCount).IsEqualTo(1);
        renderer.FailNextPipeline = true;
        await Assert.That(() => programs.Get(replacement, BlendMode.Opaque)).Throws<InvalidOperationException>();
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(1);
        programs.Get(replacement, BlendMode.Opaque);
        programs.Dispose();
        programs.Dispose();
        materials.Dispose();
        renderer.DestroyBuffer(external);
        await Assert.That(renderer.ResourceCount).IsEqualTo(0);
        await Assert.That(() => programs.Get(0, BlendMode.Opaque)).Throws<ObjectDisposedException>();
        await Assert.That(() => programs.Register(materials, shader, "vertexMain", "fragmentMain", options))
            .Throws<ObjectDisposedException>();
    }
}

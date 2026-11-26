using System.Numerics;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class MaterialResourceLifecycleTests
{
    private static PbrMaterialDesc Material() => new PbrMaterialDesc
    {
        Name = "material",
    };

    [Test]
    public async Task payloads_share_by_format_and_content_and_are_not_retained()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var textures = TexturePayloads.ColorAndData() with { Normal = TexturePayloads.Normal() };
        var first = materials.AddMaterial(Material(), textures);
        // The loader may reuse its buffers once AddMaterial returns.
        Array.Clear(textures.BaseColor!.Data);
        Array.Clear(textures.MetallicRoughness!.Data);
        Array.Clear(textures.Normal!.Data);
        var second = materials.AddMaterial(Material(), TexturePayloads.ColorAndData() with { Normal = TexturePayloads.Normal() });
        await Assert.That(materials.TextureCount).IsEqualTo(3);
        var entries = renderer.BindGroups[materials.GetBindGroup(first)].Entries.ToArray();
        await Assert.That(entries[1].Texture).IsEqualTo(entries[6].Texture);
        await Assert.That(entries[3].Texture).IsEqualTo(entries[5].Texture);
        await Assert.That(entries[1].Texture).IsNotEqualTo(entries[3].Texture);
        await Assert.That(entries[4].Texture).IsNotEqualTo(entries[1].Texture);
        await Assert.That(entries[4].Texture).IsNotEqualTo(entries[3].Texture);
        materials.ReleaseMaterial(first);
        await Assert.That(materials.TextureCount).IsEqualTo(3);
        materials.ReleaseMaterial(second);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(renderer.Textures.Count).IsEqualTo(2);
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
        var textures = TexturePayloads.ColorAndData();
        var first = materials.AddMaterial(Material(), textures);
        var second = materials.AddMaterial(Material(), textures);
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
        var textures = TexturePayloads.ColorAndData();
        var survivor = materials.AddMaterial(Material(), new PbrMaterialTextures { BaseColor = textures.BaseColor });
        var group = materials.GetBindGroup(survivor);
        var before = renderer.ResourceCount;
        var material = Material();
        if (failure == 0)
        {
            // A mip window outside its payload fails after the texture exists.
            var truncated = TexturePayloads.Normal() with { MipLevels = [new CompressedTextureMipLevel(1, 1, 0, 8, 4, 1, 1, 1)] };
            textures = textures with { Normal = truncated };
        }
        if (failure == 1) renderer.FailNextBindGroup = true;
        if (failure == 2) renderer.FailTextureWriteAfter = 0;
        await Assert.That(() => materials.AddMaterial(material, textures)).Throws<Exception>();
        await Assert.That(renderer.ResourceCount).IsEqualTo(before);
        await Assert.That(materials.MaterialCount).IsEqualTo(1);
        await Assert.That(materials.TextureCount).IsEqualTo(1);
        await Assert.That(materials.GetBindGroup(survivor)).IsEqualTo(group);

        var retry = materials.AddMaterial(Material(), TexturePayloads.ColorAndData());
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
    public async Task empty_payloads_borrow_defaults_without_destroying_them_on_release()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var defaults = renderer.Textures.Keys.ToArray();
        var material = Material();
        var textures = new PbrMaterialTextures
        {
            BaseColor = TexturePayloads.Empty(),
            MetallicRoughness = TexturePayloads.Empty(),
            Normal = TexturePayloads.Empty(),
            Occlusion = TexturePayloads.Empty(),
            Emissive = TexturePayloads.Empty(),
        };
        var first = materials.AddMaterial(material, textures);
        var second = materials.AddMaterial(material, textures);
        var entries = renderer.BindGroups[materials.GetBindGroup(first)].Entries.ToArray();
        await Assert.That(defaults.Contains(entries[1].Texture)).IsTrue();
        await Assert.That(defaults.Contains(entries[4].Texture)).IsTrue();
        await Assert.That(materials.TextureCount).IsEqualTo(0);
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
        var id = materials.AddMaterial(Material(), default, program, [BindGroupEntryDesc.ForBuffer(7, first, 0, 16)]);
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
        var id = materials.AddMaterial(Material(), default, program, [], [new MaterialTarget(7, "capture")]);
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
        var ordinary = programs.Get(program, BlendMode.Opaque, CullMode.None);
        var instanced = programs.GetInstancedPipeline(program, false, BlendMode.Opaque, CullMode.None);

        await Assert.That(() => programs.Release(otherMaterials, program))
            .Throws<InvalidOperationException>().WithMessageContaining("no registered layout");
        await Assert.That(programs.CustomProgramCount).IsEqualTo(1);
        await Assert.That(programs.Get(program, BlendMode.Opaque, CullMode.None)).IsEqualTo(ordinary);
        await Assert.That(programs.GetInstancedPipeline(program, false, BlendMode.Opaque, CullMode.None)).IsEqualTo(instanced);
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
        var id = materials.AddMaterial(Material(), default, program, [BindGroupEntryDesc.ForBuffer(7, external, 0, 16)]);
        var builtin = programs.Get(0, BlendMode.Opaque, CullMode.None);
        var custom = programs.Get(program, BlendMode.Opaque, CullMode.None);
        programs.Get(program, BlendMode.AlphaBlend, CullMode.None);
        programs.GetInstancedPipeline(program, false, BlendMode.Opaque, CullMode.None);
        programs.GetInstancedPipeline(program, false, BlendMode.AlphaBlend, CullMode.None);
        await Assert.That(() => programs.Release(materials, program)).Throws<InvalidOperationException>();
        await Assert.That(programs.Get(program, BlendMode.Opaque, CullMode.None)).IsEqualTo(custom);
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(5);

        materials.ReleaseMaterial(id);
        await Assert.That(programs.Release(materials, program)).IsTrue();
        await Assert.That(programs.Release(materials, program)).IsFalse();
        await Assert.That(programs.Release(materials, 0)).IsFalse();
        await Assert.That(programs.Release(materials, -1)).IsFalse();
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(1);
        await Assert.That(renderer.Pipelines.ContainsKey(builtin)).IsTrue();
        await Assert.That(renderer.DestroyedPipelines.Count).IsEqualTo(4);
        await Assert.That(() => programs.Get(program, BlendMode.Opaque, CullMode.None)).Throws<ArgumentException>();
        await Assert.That(() => programs.GetInstancedPipeline(program, false, BlendMode.Opaque, CullMode.None)).Throws<ArgumentException>();
        await Assert.That(() => materials.AddMaterial(Material(), default, program, [BindGroupEntryDesc.ForBuffer(7, external, 0, 16)])).Throws<ArgumentException>();

        var replacement = programs.Register(materials, shader, "vertexMain", "fragmentMain", options);
        await Assert.That(replacement).IsGreaterThan(program);
        await Assert.That(programs.CustomProgramCount).IsEqualTo(1);
        renderer.FailNextPipeline = true;
        await Assert.That(() => programs.Get(replacement, BlendMode.Opaque, CullMode.None)).Throws<InvalidOperationException>();
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(1);
        programs.Get(replacement, BlendMode.Opaque, CullMode.None);
        programs.Dispose();
        programs.Dispose();
        materials.Dispose();
        renderer.DestroyBuffer(external);
        await Assert.That(renderer.ResourceCount).IsEqualTo(0);
        await Assert.That(() => programs.Get(0, BlendMode.Opaque, CullMode.None)).Throws<ObjectDisposedException>();
        await Assert.That(() => programs.Register(materials, shader, "vertexMain", "fragmentMain", options))
            .Throws<ObjectDisposedException>();
    }
}

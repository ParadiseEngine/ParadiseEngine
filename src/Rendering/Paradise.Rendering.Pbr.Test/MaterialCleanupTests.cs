using System.Numerics;

namespace Paradise.Rendering.Pbr.Test;

public class MaterialCleanupTests
{
    [Test]
    [Arguments("group")]
    [Arguments("buffer")]
    [Arguments("texture")]
    public async Task release_attempts_every_resource_and_retires_the_id_after_a_destroy_error(string resource)
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        var baseline = backend.ResourceCount;
        var material = AddTextured(materials);
        var entries = backend.BindGroups[materials.GetBindGroup(material)].Entries.ToArray();
        var failedHandle = Resource(resource, materials.GetBindGroup(material), entries);
        var failure = new InvalidOperationException("Injected release failure.");
        var attempts = new List<object>();
        backend.AfterDestroy = handle =>
        {
            attempts.Add(handle);
            if (handle.Equals(failedHandle)) throw failure;
        };

        var error = await Assert.That(() => materials.ReleaseMaterial(material)).Throws<InvalidOperationException>();

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(attempts.Count).IsEqualTo(5); // group, uniform buffer, three distinct textures
        await Assert.That(attempts.Distinct().Count()).IsEqualTo(5);
        await Assert.That(materials.MaterialCount).IsEqualTo(0);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
        await Assert.That(materials.ReleaseMaterial(material)).IsFalse();
        await Assert.That(attempts.Count).IsEqualTo(5);
        await Assert.That(() => materials.GetBindGroup(material)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("group")]
    [Arguments("buffer")]
    public async Task failed_release_preserves_shared_owners_and_does_not_strand_texture_references(string resource)
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        var first = AddTextured(materials);
        var survivor = AddTextured(materials);
        var survivorGroup = materials.GetBindGroup(survivor);
        var entries = backend.BindGroups[materials.GetBindGroup(first)].Entries.ToArray();
        var failedHandle = Resource(resource, materials.GetBindGroup(first), entries);
        backend.AfterDestroy = handle =>
        {
            if (handle.Equals(failedHandle)) throw new InvalidOperationException("Injected shared release failure.");
        };

        await Assert.That(() => materials.ReleaseMaterial(first)).Throws<InvalidOperationException>();
        await Assert.That(materials.MaterialCount).IsEqualTo(1);
        await Assert.That(materials.TextureCount).IsEqualTo(3);
        await Assert.That(backend.DestroyedTextures.Count).IsEqualTo(0);
        await Assert.That(materials.GetBindGroup(survivor)).IsEqualTo(survivorGroup);
        await Assert.That(backend.BindGroups.ContainsKey(survivorGroup)).IsTrue();

        var replacement = AddTextured(materials);
        materials.ReleaseMaterial(survivor);
        await Assert.That(materials.TextureCount).IsEqualTo(3);
        materials.ReleaseMaterial(replacement);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(backend.Textures.Count).IsEqualTo(2);
        await Assert.That(backend.DestroyedTextures.Values.All(count => count == 1)).IsTrue();
    }

    [Test]
    public async Task release_reports_all_errors_after_attempting_each_distinct_resource_once()
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        var baseline = backend.ResourceCount;
        var material = AddTextured(materials);
        var failures = new List<Exception>();
        backend.AfterDestroy = handle =>
        {
            var failure = new InvalidOperationException($"Injected failure for {handle}.");
            failures.Add(failure);
            throw failure;
        };

        var error = await Assert.That(() => materials.ReleaseMaterial(material)).Throws<AggregateException>();
        backend.AfterDestroy = null;

        await Assert.That(failures.Count).IsEqualTo(5);
        await Assert.That(error!.InnerExceptions.SequenceEqual(failures)).IsTrue();
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(materials.MaterialCount).IsEqualTo(0);
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
        await Assert.That(materials.ReleaseMaterial(material)).IsFalse();
    }

    [Test]
    [Arguments("group")]
    [Arguments("buffer")]
    [Arguments("texture")]
    [Arguments("default-normal")]
    [Arguments("default-white")]
    [Arguments("sampler")]
    public async Task dispose_attempts_later_materials_and_defaults_even_when_one_destroy_fails(string resource)
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        var normal = backend.Textures.Single(pair => pair.Value.Name == "PbrDefaultNormal").Key;
        var white = backend.Textures.Single(pair => pair.Value.Name == "PbrDefaultWhite").Key;
        var sampler = backend.Samplers.Keys.Single();
        var first = AddTextured(materials);
        AddTextured(materials);
        materials.AddDefaultMaterial(Vector4.One);
        var entries = backend.BindGroups[materials.GetBindGroup(first)].Entries.ToArray();
        object failedHandle = resource switch
        {
            "default-normal" => normal,
            "default-white" => white,
            "sampler" => sampler,
            _ => Resource(resource, materials.GetBindGroup(first), entries),
        };
        var failure = new InvalidOperationException("Injected cache disposal failure.");
        var resourceCount = backend.ResourceCount;
        var attempts = new List<object>();
        backend.AfterDestroy = handle =>
        {
            attempts.Add(handle);
            if (handle.Equals(failedHandle)) throw failure;
        };

        var error = await Assert.That(materials.Dispose).Throws<InvalidOperationException>();

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(attempts.Count).IsEqualTo(resourceCount);
        await Assert.That(attempts.Distinct().Count()).IsEqualTo(resourceCount);
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
        await Assert.That(materials.MaterialCount).IsEqualTo(0);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(() => materials.AddDefaultMaterial(Vector4.One)).Throws<ObjectDisposedException>();
        await Assert.That(materials.ReleaseMaterial(first)).IsFalse();
        materials.Dispose();
        await Assert.That(attempts.Count).IsEqualTo(resourceCount);
    }

    [Test]
    public async Task dispose_reports_every_failure_and_is_closed_during_cleanup_callbacks()
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        AddTextured(materials);
        AddTextured(materials);
        var resourceCount = backend.ResourceCount;
        var failures = new List<Exception>();
        var closedDuringCleanup = true;
        backend.AfterDestroy = handle =>
        {
            // Native backends can invoke host callbacks. Reentry must not restart disposal.
            materials.Dispose();
            try
            {
                materials.AddDefaultMaterial(Vector4.One);
                closedDuringCleanup = false;
            }
            catch (ObjectDisposedException) { }
            var failure = new InvalidOperationException($"Injected disposal failure for {handle}.");
            failures.Add(failure);
            throw failure;
        };

        var error = await Assert.That(materials.Dispose).Throws<AggregateException>();
        backend.AfterDestroy = null;

        await Assert.That(closedDuringCleanup).IsTrue();
        await Assert.That(failures.Count).IsEqualTo(resourceCount);
        await Assert.That(error!.InnerExceptions.SequenceEqual(failures)).IsTrue();
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
        materials.Dispose();
        await Assert.That(failures.Count).IsEqualTo(resourceCount);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task dispose_during_a_frame_is_rejected_before_mutating_any_resource(bool hasMaterial)
    {
        var backend = new ResourceTrackingRenderer();
        var rendering = false;
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"))
        {
            IsFrameInProgress = () => rendering,
        };
        if (hasMaterial) materials.AddDefaultMaterial(Vector4.One);
        var baseline = backend.ResourceCount;
        rendering = true;
        try
        {
            await Assert.That(materials.Dispose).Throws<InvalidOperationException>();
            await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
        }
        finally { rendering = false; }
        materials.AddDefaultMaterial(Vector4.One);
        materials.Dispose();
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
    }

    [Test]
    public async Task renderer_disposal_continues_past_material_cleanup_errors()
    {
        var backend = new ResourceTrackingRenderer();
        using var pbr = new PbrRenderer(backend, new FeatureSwitches(), 16, 16);
        var material = pbr.Materials.AddDefaultMaterial(Vector4.One);
        var group = pbr.Materials.GetBindGroup(material);
        var (vertices, indices) = Procedural.UnitCube();
        var primitive = pbr.UploadPrimitive(vertices, indices, material);
        var failure = new InvalidOperationException("Injected material error during renderer disposal.");
        backend.AfterDestroy = handle =>
        {
            if (handle.Equals(group)) throw failure;
        };

        var error = await Assert.That(pbr.Dispose).Throws<InvalidOperationException>();

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
        await Assert.That(pbr.ReleasePrimitive(primitive)).IsFalse();
        pbr.Dispose();
        await Assert.That(backend.DestroyedBindGroups[group]).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task failed_upload_preserves_the_original_error_and_finishes_rollback_when_destroys_fail(bool textureWrite)
    {
        var backend = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr"));
        var baseline = backend.ResourceCount;
        if (textureWrite) backend.FailTextureWriteAfter = 0;
        else backend.FailNextBindGroup = true;
        var failures = new List<Exception>();
        backend.AfterDestroy = handle =>
        {
            var failure = new InvalidOperationException($"Injected rollback failure for {handle}.");
            failures.Add(failure);
            throw failure;
        };

        var error = await Assert.That(() => AddTextured(materials)).Throws<AggregateException>();
        backend.AfterDestroy = null;
        var errors = error!.Flatten().InnerExceptions;

        await Assert.That(errors.Count).IsEqualTo(failures.Count + 1);
        await Assert.That(errors.Any(failure => failure.Message == (textureWrite
            ? "Injected texture upload failure." : "Injected bind group creation failure."))).IsTrue();
        await Assert.That(failures.All(errors.Contains)).IsTrue();
        await Assert.That(materials.MaterialCount).IsEqualTo(0);
        await Assert.That(materials.TextureCount).IsEqualTo(0);
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
        var retry = AddTextured(materials);
        materials.ReleaseMaterial(retry);
        await Assert.That(backend.ResourceCount).IsEqualTo(baseline);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task failed_constructor_attempts_remaining_defaults_and_preserves_upload_and_cleanup_errors(int successfulWrites)
    {
        var backend = new ResourceTrackingRenderer { FailTextureWriteAfter = successfulWrites };
        var failures = new List<Exception>();
        backend.AfterDestroy = handle =>
        {
            var failure = new InvalidOperationException($"Injected constructor cleanup failure for {handle}.");
            failures.Add(failure);
            throw failure;
        };

        var error = await Assert.That(() => new MaterialResourceCache(backend, ShaderPrograms.Load("Shaders.pbr")))
            .Throws<AggregateException>();
        var errors = error!.Flatten().InnerExceptions;

        await Assert.That(failures.Count).IsEqualTo(successfulWrites + 2);
        await Assert.That(errors.Count).IsEqualTo(failures.Count + 1);
        await Assert.That(errors.Any(failure => failure.Message == "Injected texture upload failure.")).IsTrue();
        await Assert.That(failures.All(errors.Contains)).IsTrue();
        await Assert.That(backend.ResourceCount).IsEqualTo(0);
    }

    private static object Resource(string kind, BindGroupHandle group, BindGroupEntryDesc[] entries) => kind switch
    {
        "group" => group,
        "buffer" => entries[0].Buffer,
        "texture" => entries[4].Texture,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int AddTextured(MaterialResourceCache materials)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "color-srgb-etc1s.ktx2"));
        var textures = new PbrMaterialTextures
        {
            BaseColor = bytes, MetallicRoughness = bytes, Normal = bytes, Occlusion = bytes, Emissive = bytes,
        };
        try { return materials.AddMaterial(new PbrMaterialDesc(), textures); }
        catch (DllNotFoundException error)
        {
            Skip.Test($"libktx not loadable on this host: {error.Message}");
            throw;
        }
    }
}

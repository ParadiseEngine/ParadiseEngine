namespace Paradise.Rendering.Pbr.Test;

public sealed class MaterialShaderSpecializationTests
{
    [Test]
    public async Task ImmutableMaterialIntentRetainsOnlyItsOwnProceduralPath()
    {
        var renderer = new ResourceTrackingRenderer();
        using var materials = new MaterialResourceCache(renderer, ShaderPrograms.Load("Shaders.pbr"));
        var plain = materials.AddMaterial(new PbrMaterialDesc { ProcKind = 0 });
        var procedural = materials.AddMaterial(new PbrMaterialDesc { ProcKind = 2 });
        await Assert.That(materials.UsesProcedural(plain)).IsFalse();
        await Assert.That(materials.UsesProcedural(procedural)).IsTrue();
        materials.ReleaseMaterial(plain);
        await Assert.That(materials.UsesProcedural(procedural)).IsTrue();
    }

    [Test]
    public async Task MaterialVariantsDoNotAliasAndAllAreReleased()
    {
        var renderer = new ResourceTrackingRenderer();
        using var programs = new MaterialPrograms(renderer) { ShaderFeatures = 256 };
        var plain = programs.Get(0, BlendMode.Opaque, procedural: false);
        var noisy = programs.Get(0, BlendMode.Opaque, procedural: true);
        await Assert.That(plain).IsNotEqualTo(noisy);
        await Assert.That(programs.Get(0, BlendMode.Opaque, procedural: false)).IsEqualTo(plain);
        var plainModule = renderer.Pipelines[plain].Modules.First(module => module.Stage == ShaderStage.Fragment);
        var noisyModule = renderer.Pipelines[noisy].Modules.First(module => module.Stage == ShaderStage.Fragment);
        await Assert.That(plainModule.Constants.Span[0].Value).IsEqualTo(0d);
        await Assert.That(noisyModule.Constants.Span[0].Value).IsEqualTo(256d);
        var skinned = programs.GetSkinned(BlendMode.Opaque, procedural: false);
        var batch = programs.GetInstancedPipeline(0, false, BlendMode.Opaque, procedural: false);
        await Assert.That(skinned).IsNotEqualTo(batch);
        programs.Dispose();
        await Assert.That(renderer.Pipelines.Count).IsEqualTo(0);
    }
}

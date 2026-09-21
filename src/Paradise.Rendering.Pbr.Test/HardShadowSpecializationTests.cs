using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public sealed class HardShadowSpecializationTests
{
    [Test]
    public async Task HardOnlyFramesExcludePcssAndSoftCastingLightsRestoreIt()
    {
        var switches = new FeatureSwitches();
        using var pipeline = new RenderPipeline(8, 8, switches);
        pipeline.Add(new ShadowMarker());
        var scene = new PbrScene();
        scene.Lights.Add(new PbrLight { CastsShadows = true, SoftShadows = false });
        scene.Lights.Add(new PbrLight { CastsShadows = false, SoftShadows = true });
        pipeline.BeginFrame();
        var hard = PbrShaderSpecialization.Resolve(pipeline, scene);
        await Assert.That((hard & 1u) != 0).IsTrue();
        await Assert.That((hard & PbrShaderSpecialization.SoftShadows) == 0).IsTrue();

        scene.Lights[1] = scene.Lights[1] with { CastsShadows = true };
        var soft = PbrShaderSpecialization.Resolve(pipeline, scene);
        await Assert.That(soft).IsEqualTo(hard | PbrShaderSpecialization.SoftShadows);
        switches.Set(PbrFeatures.Shadows.Id, false);
        // Switch changes remain deferred to the next frame boundary.
        await Assert.That(PbrShaderSpecialization.Resolve(pipeline, scene)).IsEqualTo(soft);
        pipeline.BeginFrame();
        await Assert.That((PbrShaderSpecialization.Resolve(pipeline, scene) & (1u | PbrShaderSpecialization.SoftShadows)) == 0).IsTrue();
        switches.Set(PbrFeatures.Shadows.Id, true);
        pipeline.BeginFrame();
        await Assert.That(PbrShaderSpecialization.Resolve(pipeline, scene)).IsEqualTo(soft);
        scene.Lights[1] = scene.Lights[1] with { SoftShadows = false };
        await Assert.That(PbrShaderSpecialization.Resolve(pipeline, scene)).IsEqualTo(hard);
    }

    [Test]
    public async Task LightsOutsideTheUploadedFrameDoNotEnablePcss()
    {
        using var pipeline = new RenderPipeline(8, 8, new FeatureSwitches());
        pipeline.Add(new ShadowMarker());
        pipeline.BeginFrame();
        var scene = new PbrScene();
        for (var i = 0; i < FrameUniformsGpu.MaxSceneLights; i++)
            scene.Lights.Add(new PbrLight { CastsShadows = true });
        scene.Lights.Add(new PbrLight { CastsShadows = true, SoftShadows = true });
        await Assert.That((PbrShaderSpecialization.Resolve(pipeline, scene) & PbrShaderSpecialization.SoftShadows) == 0).IsTrue();
    }

    [Test]
    public async Task MaterialTextureMasksPreserveSoftShadowVariantsForAllGeometryPaths()
    {
        var backend = new ResourceTrackingRenderer();
        using var programs = new MaterialPrograms(backend);
        programs.ShaderFeatures = PbrShaderSpecialization.All;
        var soft = new[]
        {
            programs.Get(0, BlendMode.Opaque, procedural: false, textureFeatures: 0),
            programs.GetSkinned(BlendMode.Opaque, procedural: false, textureFeatures: 0),
            programs.GetInstancedPipeline(0, false, BlendMode.Opaque, procedural: false, textureFeatures: 0),
        };
        programs.ShaderFeatures &= ~PbrShaderSpecialization.SoftShadows;
        var hard = new[]
        {
            programs.Get(0, BlendMode.Opaque, procedural: false, textureFeatures: 0),
            programs.GetSkinned(BlendMode.Opaque, procedural: false, textureFeatures: 0),
            programs.GetInstancedPipeline(0, false, BlendMode.Opaque, procedural: false, textureFeatures: 0),
        };
        for (var i = 0; i < soft.Length; i++)
        {
            await Assert.That(soft[i] == hard[i]).IsFalse();
            foreach (var handle in new[] { soft[i], hard[i] })
            {
                var constants = backend.Pipelines[handle].Modules
                    .Where(module => (module.Stage & ShaderStage.Fragment) != 0)
                    .SelectMany(module => module.Constants.ToArray());
                var overrides = constants.Where(value => value.Key == PbrShaderSpecialization.OverrideKey).ToArray();
                await Assert.That(overrides.Length).IsGreaterThan(0);
                foreach (var value in overrides)
                {
                    var mask = (uint)value.Value;
                    await Assert.That((mask & PbrShaderSpecialization.SoftShadows) != 0).IsEqualTo(handle == soft[i]);
                    await Assert.That((mask & PbrShaderSpecialization.TextureBits) == 0).IsTrue();
                }
            }
        }
    }

    private sealed class ShadowMarker : IRenderFeature
    {
        public FeatureDefinition Definition => PbrFeatures.Shadows;
        public FrameRequirements Requires => FrameRequirements.None;
        public void Resize(uint width, uint height) { }
        public void Setup(in FrameContext frame) { }
        public void Dispose() { }
    }
}

using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class PbrShaderSpecializationTests
{
    [Test]
    public async Task SpecializationPreservesLayoutsAndUnrelatedConstants()
    {
        var fragment = new ShaderModuleDesc("@id(19000) override pbrEnabledFeatures_0: u32 = 511u;", "fragmentMain", ShaderStage.Fragment)
        { Constants = new ShaderConstant[] { new("custom", 3) } };
        var vertex = new ShaderModuleDesc("", "vertexMain", ShaderStage.Vertex);
        var program = new ShaderProgramDesc([vertex, fragment], new PipelineLayoutDesc([], []), []);
        var specialized = PbrShaderSpecialization.Apply(program, 256);
        await Assert.That(ReferenceEquals(specialized.Layout, program.Layout)).IsTrue();
        await Assert.That(ReferenceEquals(specialized.Modules[0], vertex)).IsTrue();
        await Assert.That(specialized.Modules[1].Constants.ToArray()).IsEquivalentTo(new ShaderConstant[] { new("custom", 3), new("19000", 256) });
        await Assert.That(fragment.Constants.Length).IsEqualTo(1);
    }

    [Test]
    public async Task OldCustomShadersRemainUnchanged()
    {
        var program = new ShaderProgramDesc([new ShaderModuleDesc("old WGSL", "fragmentMain", ShaderStage.Fragment)], new PipelineLayoutDesc([], []), []);
        await Assert.That(ReferenceEquals(program, PbrShaderSpecialization.Apply(program, 0))).IsTrue();
    }

    [Test]
    public async Task MinimalFrameRemovesOptionalPathsWithoutRemovingMaterialRecipes()
    {
        var switches = new FeatureSwitches();
        PbrFeatures.DeclareAll(switches);
        foreach (var feature in PbrFeatures.All) switches.Set(feature.Id, false);
        var pipeline = new RenderPipeline(8, 8, switches);
        var scene = new PbrScene();
        pipeline.BeginFrame();
        await Assert.That(PbrShaderSpecialization.Resolve(pipeline, scene)).IsEqualTo(256u | PbrShaderSpecialization.TextureBits);
        pipeline.Dispose();
    }
}

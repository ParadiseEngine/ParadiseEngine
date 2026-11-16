using System.Runtime.CompilerServices;

namespace Paradise.Rendering.Sdf.Test;

/// <summary>Checks shader-reflected SDF layouts against the CPU mirrors; needs no GPU.</summary>
public class SdfUniformLayoutTests
{
    private static ShaderProgramDesc Load(string name) =>
        ShaderProgramLoader.Load(typeof(SdfRenderer).Assembly, name);

    [Test]
    public async Task mirrors_match_the_reflected_layouts()
    {
        var scene = Load("Shaders.sdfScene");
        var sceneReflections = Load("Shaders.sdfSceneReflections");
        var sceneProbes = Load("Shaders.sdfSceneProbes");
        var sceneBoth = Load("Shaders.sdfSceneProbesReflections");
        var probeTrace = Load("Shaders.sdfProbeTrace");
        var probeBlend = Load("Shaders.sdfProbeBlend");
        var bloom = Load("Shaders.sdfBloom");
        var composite = Load("Shaders.sdfComposite");
        var fxaa = Load("Shaders.sdfFxaa");

        SdfUniformLayout.ValidateScene(scene, probes: false);
        SdfUniformLayout.ValidateScene(sceneReflections, probes: false);
        SdfUniformLayout.ValidateScene(sceneProbes, probes: true);
        SdfUniformLayout.ValidateScene(sceneBoth, probes: true);
        SdfUniformLayout.ValidateScene(probeTrace, probes: true);
        SdfUniformLayout.ValidateGi(probeBlend);
        SdfUniformLayout.ValidateBloom(bloom);
        SdfUniformLayout.ValidateComposite(composite);
        SdfUniformLayout.ValidateFxaa(fxaa);
        // The frame, and the probes where the program lights from them; every other pass has
        // one block of its own.
        await Assert.That(scene.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(sceneReflections.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(sceneProbes.UniformBlocks.Length).IsEqualTo(2);
        await Assert.That(sceneBoth.UniformBlocks.Length).IsEqualTo(2);
        await Assert.That(probeTrace.UniformBlocks.Length).IsEqualTo(2);
        await Assert.That(probeBlend.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(bloom.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(composite.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(fxaa.UniformBlocks.Length).IsEqualTo(1);
    }

    [Test]
    public async Task struct_sizes_match_wgsl_totals()
    {
        await Assert.That(Unsafe.SizeOf<SdfShapeGpu>()).IsEqualTo(160);
        await Assert.That(Unsafe.SizeOf<SdfLightGpu>()).IsEqualTo(64);
        await Assert.That(Unsafe.SizeOf<SdfFrameUniformsGpu>()).IsEqualTo(272 + SdfRenderer.MaxLights * 64);
        await Assert.That(Unsafe.SizeOf<SdfGiUniformsGpu>()).IsEqualTo(128);
        await Assert.That(Unsafe.SizeOf<SdfBloomUniformsGpu>()).IsEqualTo(16);
        await Assert.That(Unsafe.SizeOf<SdfCompositeUniformsGpu>()).IsEqualTo(16);
        await Assert.That(Unsafe.SizeOf<SdfFxaaUniformsGpu>()).IsEqualTo(32);
    }

    [Test]
    [Arguments("Shaders.sdfScene", false)]
    [Arguments("Shaders.sdfSceneReflections", false)]
    [Arguments("Shaders.sdfSceneProbes", true)]
    [Arguments("Shaders.sdfSceneProbesReflections", true)]
    [Arguments("Shaders.sdfProbeTrace", true)]
    public async Task field_programs_bind_the_frame_and_shapes_and_the_probes_only_where_they_light_from_them(string name, bool probes)
    {
        var program = Load(name);

        // The same bindings in every pass that reads the field, so they share the scene's
        // buffers; a program without probes declares nothing of them.
        await Assert.That(program.Layout.Groups.Length).IsEqualTo(1);
        var entries = program.Layout.Groups[0].Entries;
        await Assert.That(entries.Select(entry => entry.Binding).SequenceEqual(probes ? [0u, 1u, 2u, 3u] : [0u, 1u])).IsTrue();
        await Assert.That(entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(entries[1].Type).IsEqualTo(BindingResourceType.ReadonlyStorageBuffer);
        if (probes) await Assert.That(entries[3].Type).IsEqualTo(BindingResourceType.UniformBuffer);
    }

    [Test]
    public async Task validator_catches_a_doctored_field_offset()
    {
        var program = Load("Shaders.sdfScene");
        var blocks = (UniformBlockDesc[])program.UniformBlocks.Clone();
        var index = Array.FindIndex(blocks, block => block.Name == "sdfFrame");
        var fields = (UniformFieldDesc[])blocks[index].Fields.Clone();
        fields[2] = fields[2] with { Offset = fields[2].Offset + 4 };
        blocks[index] = blocks[index] with { Fields = fields };
        var doctored = new ShaderProgramDesc(program.Modules, program.Layout, program.VertexBuffers) { UniformBlocks = blocks };

        // Everything but the one offset is intact, so the offset is what it objects to.
        var error = Assert.Throws<InvalidOperationException>(() => SdfUniformLayout.ValidateScene(doctored, probes: false));
        await Assert.That(error.Message).Contains("'counts'@128+16");
    }
}

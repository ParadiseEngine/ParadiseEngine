using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Reflection round-trip test for the M1 design contract: load `triangle.reflection.json`
/// from this assembly's embedded resources (slangc emits it during the test build via Slang.targets),
/// run it through <see cref="ShaderProgramLoader"/>, and assert the vertex layout matches what
/// `triangle.slang` declares for VsIn (POSITION = float2, COLOR = float3 — 20-byte stride). Catches
/// Slang regressions like upstream issues #5222 / #5612 immediately on every PR.</summary>
public class ShaderProgramLoaderTests
{
    [Test]
    public async Task triangle_reflection_round_trips_to_expected_vertex_layout()
    {
        var assembly = typeof(ShaderProgramLoaderTests).Assembly;
        var program = ShaderProgramLoader.Load(assembly, "Shaders.triangle");

        // Two modules: vertex + fragment. The loader carries both in the program record so the
        // pipeline build can pick the right entry point per stage.
        await Assert.That(program.Modules.Length).IsEqualTo(2);

        ShaderModuleDesc? vs = null;
        ShaderModuleDesc? fs = null;
        foreach (var m in program.Modules)
        {
            if (m.Stage == ShaderStage.Vertex) vs = m;
            if (m.Stage == ShaderStage.Fragment) fs = m;
        }
        await Assert.That(vs).IsNotNull();
        await Assert.That(fs).IsNotNull();
        await Assert.That(vs!.EntryPoint).IsEqualTo("vs_main");
        await Assert.That(fs!.EntryPoint).IsEqualTo("fs_main");

        // Vertex layout MUST come from reflection — assert the shape that triangle.slang declares.
        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);
        var vb = program.VertexBuffers[0];
        await Assert.That(vb.StepMode).IsEqualTo(VertexStepMode.Vertex);
        await Assert.That(vb.Stride).IsEqualTo(20ul);  // float2 + float3 = 8 + 12

        await Assert.That(vb.Attributes.Length).IsEqualTo(2);
        await Assert.That(vb.Attributes[0].ShaderLocation).IsEqualTo(0u);
        await Assert.That(vb.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x2);
        await Assert.That(vb.Attributes[0].Offset).IsEqualTo(0ul);
        await Assert.That(vb.Attributes[1].ShaderLocation).IsEqualTo(1u);
        await Assert.That(vb.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x3);
        await Assert.That(vb.Attributes[1].Offset).IsEqualTo(8ul);
    }

    [Test]
    public async Task wgsl_module_source_is_non_empty()
    {
        var assembly = typeof(ShaderProgramLoaderTests).Assembly;
        var program = ShaderProgramLoader.Load(assembly, "Shaders.triangle");
        await Assert.That(program.Modules[0].Wgsl.Length).IsGreaterThan(0);
        await Assert.That(program.Modules[0].Wgsl).Contains("@vertex");
    }
}

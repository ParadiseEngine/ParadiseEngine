using System;

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

    [Test]
    public async Task two_loads_produce_modules_with_identical_wgsl_and_distinct_records()
    {
        // Repeated loads must preserve the (WGSL, entry point, stage) content key so native shader
        // caching can reuse modules.
        var assembly = typeof(ShaderProgramLoaderTests).Assembly;
        var p1 = ShaderProgramLoader.Load(assembly, "Shaders.triangle");
        var p2 = ShaderProgramLoader.Load(assembly, "Shaders.triangle");
        await Assert.That(ReferenceEquals(p1, p2)).IsFalse();
        await Assert.That(p1.Modules.Length).IsEqualTo(p2.Modules.Length);
        for (var i = 0; i < p1.Modules.Length; i++)
        {
            await Assert.That(p1.Modules[i].Wgsl).IsEqualTo(p2.Modules[i].Wgsl);
            await Assert.That(p1.Modules[i].EntryPoint).IsEqualTo(p2.Modules[i].EntryPoint);
            await Assert.That(p1.Modules[i].Stage).IsEqualTo(p2.Modules[i].Stage);
        }
    }

    [Test]
    public async Task build_program_desc_rejects_unsupported_global_parameter_kinds()
    {
        // The successor of the M1 "reject non-varying bindings" guard: global parameters that
        // the binding pipeline can't map (push constants, storage textures, …) must throw a
        // descriptive NotSupportedException instead of silently emitting a layout that omits
        // them (a shader whose bindings the engine can't validate or bind against).
        var reflection = new SlangReflection(
            EntryPoints: Array.Empty<SlangEntryPoint>(),
            Parameters: new[]
            {
                new SlangParameter(
                    Name: "pushed",
                    Binding: new SlangBinding(Kind: "pushConstantBuffer", Index: 0, Count: null),
                    Type: new SlangTypeNode(Kind: "constantBuffer", Name: null, Fields: null, ElementCount: null, ElementType: null, ScalarType: null),
                    SemanticName: null),
            });

        await Assert.That(() => ShaderProgramLoader.BuildProgramDesc("// no wgsl\n", reflection))
            .Throws<NotSupportedException>();

        var badType = new SlangReflection(
            EntryPoints: Array.Empty<SlangEntryPoint>(),
            Parameters: new[]
            {
                new SlangParameter(
                    Name: "storageTex",
                    Binding: new SlangBinding(Kind: "descriptorTableSlot", Index: 0, Count: null),
                    Type: new SlangTypeNode(Kind: "resource", Name: null, Fields: null, ElementCount: null, ElementType: null, ScalarType: null, BaseShape: "texture3D"),
                    SemanticName: null),
            });

        await Assert.That(() => ShaderProgramLoader.BuildProgramDesc("// no wgsl\n", badType))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task build_program_desc_accepts_varying_input_and_output()
    {
        // Sibling positive-case: an entry point whose parameters are pure varyingInput /
        // varyingOutput (the triangle shape) loads cleanly without tripping the binding guard.
        var reflection = new SlangReflection(EntryPoints: new[]
        {
            new SlangEntryPoint(
                Name: "vs_main",
                Stage: "vertex",
                Parameters: new[]
                {
                    new SlangParameter(
                        Name: "input",
                        Binding: new SlangBinding(Kind: "varyingInput", Index: 0, Count: 1),
                        Type: new SlangTypeNode(Kind: "struct", Name: "VsIn", Fields: Array.Empty<SlangField>(), ElementCount: null, ElementType: null, ScalarType: null),
                        SemanticName: null),
                    new SlangParameter(
                        Name: "output",
                        Binding: new SlangBinding(Kind: "varyingOutput", Index: 0, Count: 1),
                        Type: new SlangTypeNode(Kind: "struct", Name: "VsOut", Fields: Array.Empty<SlangField>(), ElementCount: null, ElementType: null, ScalarType: null),
                        SemanticName: null),
                }),
        });

        var program = ShaderProgramLoader.BuildProgramDesc("// no wgsl\n", reflection);
        await Assert.That(program.Modules.Length).IsEqualTo(1);
        await Assert.That(program.Layout.Groups.Length).IsEqualTo(0);
        await Assert.That(program.Layout.PushConstants.Length).IsEqualTo(0);
    }
}

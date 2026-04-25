using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Load material.reflection.json out of the test assembly (slangc produced it from the
/// Shaders/material.slang fixture during the build), run it through
/// <see cref="ShaderProgramLoader"/>, and assert the resulting bind-group layout structure matches
/// what material.slang declares: group 0 = uniform buffer (Time), group 1 = sampled texture +
/// sampler. This is the load-bearing M2 contract — if Slang regresses the reflection shape for
/// textures + samplers + uniform buffers, the test breaks and the shader bindings silently failing
/// downstream becomes impossible.</summary>
public class MaterialReflectionTests
{
    [Test]
    public async Task material_reflection_produces_frame_and_material_bind_groups()
    {
        var assembly = typeof(MaterialReflectionTests).Assembly;
        var program = ShaderProgramLoader.Load(assembly, "Shaders.material");

        await Assert.That(program.Modules.Length).IsEqualTo(2);
        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);

        var vb = program.VertexBuffers[0];
        await Assert.That(vb.Stride).IsEqualTo(16ul); // float2 pos + float2 uv = 16 bytes
        await Assert.That(vb.Attributes.Length).IsEqualTo(2);
        await Assert.That(vb.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x2);
        await Assert.That(vb.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x2);

        await Assert.That(program.Layout.Groups.Length).IsEqualTo(2);

        BindGroupLayoutDesc? frame = null;
        BindGroupLayoutDesc? material = null;
        foreach (var g in program.Layout.Groups)
        {
            if (g.GroupIndex == 0) frame = g;
            else if (g.GroupIndex == 1) material = g;
        }
        await Assert.That(frame).IsNotNull();
        await Assert.That(material).IsNotNull();

        // Group 0: FrameUniforms — one uniform buffer at binding 0. The std140-padded element size
        // is 16 bytes (a single float + 12 bytes of tail padding); the reflection-derived
        // MinBufferSize reflects that (not the raw 4-byte field size) so Dawn's validation against
        // the actual buffer min-size lines up on the non-trivial binding path.
        await Assert.That(frame!.Entries.Length).IsEqualTo(1);
        await Assert.That(frame.Entries[0].Binding).IsEqualTo(0u);
        await Assert.That(frame.Entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(frame.Entries[0].MinBufferSize).IsEqualTo(16ul);
        // Slang's reflection conservatively lists every module-level binding under every entry
        // point's `bindings[]` array (not just the ones the entry point actually reads), so
        // visibility infers to Vertex | Fragment for every bound resource in a two-stage program.
        await Assert.That(frame.Entries[0].Visibility).IsEqualTo(ShaderStage.Vertex | ShaderStage.Fragment);

        // Group 1: Material — sampled texture at binding 0, sampler at binding 1 (explicit
        // [[vk::binding(...)]] on the sampler disambiguates WGSL bindings within the group).
        await Assert.That(material!.Entries.Length).IsEqualTo(2);
        // Entries are sorted by binding number.
        await Assert.That(material.Entries[0].Binding).IsEqualTo(0u);
        await Assert.That(material.Entries[0].Type).IsEqualTo(BindingResourceType.SampledTexture);
        await Assert.That(material.Entries[1].Binding).IsEqualTo(1u);
        await Assert.That(material.Entries[1].Type).IsEqualTo(BindingResourceType.Sampler);
    }

    [Test]
    public async Task material_reflection_has_no_push_constants()
    {
        var assembly = typeof(MaterialReflectionTests).Assembly;
        var program = ShaderProgramLoader.Load(assembly, "Shaders.material");
        await Assert.That(program.Layout.PushConstants.Length).IsEqualTo(0);
    }
}

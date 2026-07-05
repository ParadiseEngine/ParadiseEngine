using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>THE golden contract test for the M2 binding pipeline: <c>Shaders/bindings.slang</c>
/// is compiled by the real slangc at build time, and this suite pins the shape the loader
/// derives from its reflection JSON — bind-group layouts (groups/bindings/kinds/min sizes) and
/// uniform-block byte layouts (per-field offset/size, WGSL rules). If a slangc upgrade changes
/// the reflection schema or the uniform layout rules, these tests break in CI rather than a
/// frame corrupting at runtime. Uniform consumers (mirror structs) validate against the same
/// UniformBlockDesc data these tests assert.</summary>
public class BindingsReflectionGoldenTests
{
    private static ShaderProgramDesc Load() =>
        ShaderProgramLoader.Load(typeof(BindingsReflectionGoldenTests).Assembly, "Shaders.bindings");

    [Test]
    public async Task layout_has_two_groups_with_expected_bindings()
    {
        var program = Load();
        var groups = program.Layout.Groups;
        await Assert.That(groups.Length).IsEqualTo(2);

        // Group 0: the draw constant buffer (slangc omits `space` for group 0).
        await Assert.That(groups[0].GroupIndex).IsEqualTo(0u);
        await Assert.That(groups[0].Entries.Length).IsEqualTo(1);
        await Assert.That(groups[0].Entries[0].Binding).IsEqualTo(0u);
        await Assert.That(groups[0].Entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(groups[0].Entries[0].MinBufferSize).IsEqualTo(80ul); // mat4 + float4
        await Assert.That(groups[0].Entries[0].HasDynamicOffset).IsFalse();    // opt-in, never reflected

        // Group 1: frame constant buffer + texture + sampler, ordered by binding index.
        await Assert.That(groups[1].GroupIndex).IsEqualTo(1u);
        await Assert.That(groups[1].Entries.Length).IsEqualTo(3);
        await Assert.That(groups[1].Entries[0].Binding).IsEqualTo(0u);
        await Assert.That(groups[1].Entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(groups[1].Entries[0].MinBufferSize).IsEqualTo(240ul);
        await Assert.That(groups[1].Entries[1].Binding).IsEqualTo(1u);
        await Assert.That(groups[1].Entries[1].Type).IsEqualTo(BindingResourceType.SampledTexture);
        await Assert.That(groups[1].Entries[2].Binding).IsEqualTo(2u);
        await Assert.That(groups[1].Entries[2].Type).IsEqualTo(BindingResourceType.Sampler);

        // Visibility is over-approximated to VS|FS by design (slangc's per-entry-point binding
        // lists include ALL globals, so per-stage attribution is not derivable).
        foreach (var group in groups)
        {
            foreach (var entry in group.Entries)
            {
                await Assert.That(entry.Visibility).IsEqualTo(ShaderStage.Vertex | ShaderStage.Fragment);
            }
        }
    }

    [Test]
    public async Task uniform_blocks_carry_wgsl_field_offsets()
    {
        var program = Load();
        await Assert.That(program.UniformBlocks.Length).IsEqualTo(2);

        var draw = program.UniformBlocks[0];
        await Assert.That(draw.Name).IsEqualTo("drawParams");
        await Assert.That(draw.Group).IsEqualTo(0u);
        await Assert.That(draw.Binding).IsEqualTo(0u);
        await Assert.That(draw.SizeBytes).IsEqualTo(80u);
        await Assert.That(draw.Fields.Length).IsEqualTo(2);
        await Assert.That(draw.Fields[0].Name).IsEqualTo("model");
        await Assert.That(draw.Fields[0].Offset).IsEqualTo(0u);
        await Assert.That(draw.Fields[0].Size).IsEqualTo(64u);
        await Assert.That(draw.Fields[1].Name).IsEqualTo("tint");
        await Assert.That(draw.Fields[1].Offset).IsEqualTo(64u);
        await Assert.That(draw.Fields[1].Size).IsEqualTo(16u);

        var frame = program.UniformBlocks[1];
        await Assert.That(frame.Name).IsEqualTo("frameParams");
        await Assert.That(frame.Group).IsEqualTo(1u);
        await Assert.That(frame.SizeBytes).IsEqualTo(240u);

        // Field-by-field: float4x4 (64) + float4 (16) + float4 (16) + PointLight[4] (4×32=128,
        // one entry with the TOTAL size) + 4 scalar floats. Offsets are the WGSL layout slangc
        // reflected — never hand-derived.
        var expected = new (string Name, uint Offset, uint Size)[]
        {
            ("viewProjection", 0, 64),
            ("cameraPosition", 64, 16),
            ("ambientColor", 80, 16),
            ("lights", 96, 128),
            ("exposure", 224, 4),
            ("pad0", 228, 4),
            ("pad1", 232, 4),
            ("pad2", 236, 4),
        };
        await Assert.That(frame.Fields.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(frame.Fields[i].Name).IsEqualTo(expected[i].Name);
            await Assert.That(frame.Fields[i].Offset).IsEqualTo(expected[i].Offset);
            await Assert.That(frame.Fields[i].Size).IsEqualTo(expected[i].Size);
        }
    }

    [Test]
    public async Task vertex_layout_still_derives_from_reflection_with_globals_present()
    {
        var program = Load();
        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);
        var vb = program.VertexBuffers[0];
        await Assert.That(vb.Stride).IsEqualTo(20ul); // float3 + float2
        await Assert.That(vb.Attributes.Length).IsEqualTo(2);
        await Assert.That(vb.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x3);
        await Assert.That(vb.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x2);
        await Assert.That(vb.Attributes[1].Offset).IsEqualTo(12ul);
    }
}

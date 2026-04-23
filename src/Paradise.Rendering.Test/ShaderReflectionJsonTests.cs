using System.IO;
using System.Text.Json;

namespace Paradise.Rendering.Test;

public class ShaderReflectionJsonTests
{
    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "reflection.sample.json");

    private static ShaderProgramDesc DeserializeFixture()
    {
        var json = File.ReadAllText(FixturePath());
        var program = JsonSerializer.Deserialize(json, ShaderReflectionJsonContext.Default.ShaderProgramDesc);
        if (program is null) throw new InvalidOperationException("Fixture failed to deserialize.");
        return program;
    }

    [Test]
    public async Task fixture_deserializes_into_shader_program_desc()
    {
        var program = DeserializeFixture();

        await Assert.That(program.Modules.Length).IsEqualTo(2);
        await Assert.That(program.Modules[0].EntryPoint).IsEqualTo("vs_main");
        await Assert.That(program.Modules[0].Stage).IsEqualTo(ShaderStage.Vertex);
        await Assert.That(program.Modules[1].EntryPoint).IsEqualTo("fs_main");
        await Assert.That(program.Modules[1].Stage).IsEqualTo(ShaderStage.Fragment);
    }

    [Test]
    public async Task fixture_pipeline_layout_has_one_group_with_uniform_entry()
    {
        var program = DeserializeFixture();
        await Assert.That(program.Layout.Groups.Length).IsEqualTo(1);
        var group = program.Layout.Groups[0];
        await Assert.That(group.GroupIndex).IsEqualTo(0u);
        await Assert.That(group.Entries.Length).IsEqualTo(1);

        var entry = group.Entries[0];
        await Assert.That(entry.Binding).IsEqualTo(0u);
        await Assert.That(entry.Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(entry.MinBufferSize).IsEqualTo(64ul);
        // Direct flag-equality assertion (per OpenCara minor risk on STJ flags round-trip):
        // makes the comma-separated "Vertex, Fragment" -> ShaderStage.Vertex|Fragment path
        // an explicit invariant rather than relying on bitwise spot-checks.
        await Assert.That(entry.Visibility).IsEqualTo(ShaderStage.Vertex | ShaderStage.Fragment);
    }

    [Test]
    public async Task fixture_push_constants_round_trip()
    {
        var program = DeserializeFixture();
        await Assert.That(program.Layout.PushConstants.Length).IsEqualTo(1);
        var pc = program.Layout.PushConstants[0];
        await Assert.That(pc.Visibility).IsEqualTo(ShaderStage.Vertex);
        await Assert.That(pc.Offset).IsEqualTo(0u);
        await Assert.That(pc.Size).IsEqualTo(16u);
    }

    [Test]
    public async Task fixture_vertex_buffer_layout_matches_expected_attributes()
    {
        var program = DeserializeFixture();
        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);
        var vb = program.VertexBuffers[0];
        await Assert.That(vb.Stride).IsEqualTo(20ul);
        await Assert.That(vb.StepMode).IsEqualTo(VertexStepMode.Vertex);
        await Assert.That(vb.Attributes.Length).IsEqualTo(2);

        await Assert.That(vb.Attributes[0].ShaderLocation).IsEqualTo(0u);
        await Assert.That(vb.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x2);
        await Assert.That(vb.Attributes[0].Offset).IsEqualTo(0ul);

        await Assert.That(vb.Attributes[1].ShaderLocation).IsEqualTo(1u);
        await Assert.That(vb.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x3);
        await Assert.That(vb.Attributes[1].Offset).IsEqualTo(8ul);
    }

    [Test]
    public async Task serialize_then_deserialize_preserves_structural_equality()
    {
        var original = DeserializeFixture();
        var json = JsonSerializer.Serialize(original, ShaderReflectionJsonContext.Default.ShaderProgramDesc);
        var roundTripped = JsonSerializer.Deserialize(json, ShaderReflectionJsonContext.Default.ShaderProgramDesc);
        if (roundTripped is null) throw new InvalidOperationException("Round-trip deserialization returned null.");

        await AssertProgramsEqualAsync(original, roundTripped).ConfigureAwait(false);
    }

    private static async Task AssertProgramsEqualAsync(ShaderProgramDesc a, ShaderProgramDesc b)
    {
        await Assert.That(a.Modules.Length).IsEqualTo(b.Modules.Length);
        for (var i = 0; i < a.Modules.Length; i++)
        {
            await Assert.That(a.Modules[i]).IsEqualTo(b.Modules[i]);
        }

        await Assert.That(a.Layout.Groups.Length).IsEqualTo(b.Layout.Groups.Length);
        for (var i = 0; i < a.Layout.Groups.Length; i++)
        {
            var ag = a.Layout.Groups[i];
            var bg = b.Layout.Groups[i];
            await Assert.That(ag.GroupIndex).IsEqualTo(bg.GroupIndex);
            await Assert.That(ag.Entries.Length).IsEqualTo(bg.Entries.Length);
            for (var j = 0; j < ag.Entries.Length; j++)
            {
                await Assert.That(ag.Entries[j]).IsEqualTo(bg.Entries[j]);
            }
        }

        await Assert.That(a.Layout.PushConstants.Length).IsEqualTo(b.Layout.PushConstants.Length);
        for (var i = 0; i < a.Layout.PushConstants.Length; i++)
        {
            await Assert.That(a.Layout.PushConstants[i]).IsEqualTo(b.Layout.PushConstants[i]);
        }

        await Assert.That(a.VertexBuffers.Length).IsEqualTo(b.VertexBuffers.Length);
        for (var i = 0; i < a.VertexBuffers.Length; i++)
        {
            var av = a.VertexBuffers[i];
            var bv = b.VertexBuffers[i];
            await Assert.That(av.Stride).IsEqualTo(bv.Stride);
            await Assert.That(av.StepMode).IsEqualTo(bv.StepMode);
            await Assert.That(av.Attributes.Length).IsEqualTo(bv.Attributes.Length);
            for (var j = 0; j < av.Attributes.Length; j++)
            {
                await Assert.That(av.Attributes[j]).IsEqualTo(bv.Attributes[j]);
            }
        }
    }
}

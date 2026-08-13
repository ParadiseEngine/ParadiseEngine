namespace Paradise.Rendering.Test;

/// <summary>Guards the property that moved <see cref="ShaderProgramLoader"/> out of the WebGPU
/// backend and into this package: loading a build-time-compiled WGSL + reflection-JSON resource
/// pair needs NOTHING but Paradise.Rendering. This test project references no backend at all, so
/// if the loader ever regains a backend dependency, this suite stops compiling — which is the
/// whole point of it living here.</summary>
/// <remarks>The fixture pair under <c>Shaders/</c> is hand-written rather than slangc output; the
/// schema-drift golden tests that need real slangc output stay in Paradise.Rendering.WebGPU.Test,
/// next to the .slang sources and the headless renderer that consumes them.</remarks>
public class ShaderProgramLoaderTests
{
    private static ShaderProgramDesc Load() =>
        ShaderProgramLoader.Load(typeof(ShaderProgramLoaderTests).Assembly, "Shaders.minimal");

    [Test]
    public async Task load_reads_the_embedded_resource_pair_without_a_backend()
    {
        var program = Load();

        // One module per entry point, each carrying the same WGSL blob — the stage + entry-point
        // name is what selects which half a pipeline compiles.
        await Assert.That(program.Modules.Length).IsEqualTo(2);
        await Assert.That(program.Modules[0].EntryPoint).IsEqualTo("vertexMain");
        await Assert.That(program.Modules[0].Stage).IsEqualTo(ShaderStage.Vertex);
        await Assert.That(program.Modules[1].EntryPoint).IsEqualTo("fragmentMain");
        await Assert.That(program.Modules[1].Stage).IsEqualTo(ShaderStage.Fragment);
        await Assert.That(program.Modules[0].Wgsl).IsEqualTo(program.Modules[1].Wgsl);
        await Assert.That(program.Modules[0].Wgsl).Contains("fn vertexMain");
    }

    [Test]
    public async Task vertex_layout_comes_from_reflection_not_hand_coding()
    {
        var program = Load();

        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);
        var buffer = program.VertexBuffers[0];
        // float3 position + float2 uv, packed in declaration order: 12 + 8 = 20 bytes.
        await Assert.That(buffer.Stride).IsEqualTo(20ul);
        await Assert.That(buffer.StepMode).IsEqualTo(VertexStepMode.Vertex);
        await Assert.That(buffer.Attributes.Length).IsEqualTo(2);
        await Assert.That(buffer.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x3);
        await Assert.That(buffer.Attributes[0].Offset).IsEqualTo(0ul);
        await Assert.That(buffer.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x2);
        await Assert.That(buffer.Attributes[1].Offset).IsEqualTo(12ul);

        // The per-entry-point map must carry the same layout: dropping it repoints skinned
        // pipelines at a rigid stride and the draws silently produce nothing.
        await Assert.That(program.VertexBuffersByEntryPoint.ContainsKey("vertexMain")).IsTrue();
        await Assert.That(program.VertexBuffersByEntryPoint["vertexMain"][0].Stride).IsEqualTo(20ul);
    }

    [Test]
    public async Task constant_buffer_becomes_a_bind_group_entry_plus_a_uniform_block()
    {
        var program = Load();

        await Assert.That(program.Layout.Groups.Length).IsEqualTo(1);
        var entries = program.Layout.Groups[0].Entries;
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(entries[0].MinBufferSize).IsEqualTo(80u);

        // Uniform blocks are the offset table mirror-struct validators check against.
        await Assert.That(program.UniformBlocks.Length).IsEqualTo(1);
        var block = program.UniformBlocks[0];
        await Assert.That(block.SizeBytes).IsEqualTo(80u);
        await Assert.That(block.Fields.Length).IsEqualTo(2);
        await Assert.That(block.Fields[1].Name).IsEqualTo("tint");
        await Assert.That(block.Fields[1].Offset).IsEqualTo(64u);
    }

    [Test]
    public async Task a_missing_resource_names_the_assembly_and_lists_what_is_there()
    {
        // The failure mode this guards is a build that silently stops embedding the pair (a
        // renamed folder, a dropped Slang.targets import): without the listing the exception says
        // only "not found" and every caller looks like a typo.
        await Assert.That(() => ShaderProgramLoader.Load(typeof(ShaderProgramLoaderTests).Assembly, "Shaders.absent"))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Shaders.minimal.wgsl");
    }
}

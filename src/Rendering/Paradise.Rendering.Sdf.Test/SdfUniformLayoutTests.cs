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
        var composite = Load("Shaders.sdfComposite");

        SdfUniformLayout.ValidateScene(scene);
        SdfUniformLayout.ValidateComposite(composite);
        await Assert.That(scene.UniformBlocks.Length).IsEqualTo(1);
        await Assert.That(composite.UniformBlocks.Length).IsEqualTo(1);
    }

    [Test]
    public async Task struct_sizes_match_wgsl_totals()
    {
        await Assert.That(Unsafe.SizeOf<SdfShapeGpu>()).IsEqualTo(144);
        await Assert.That(Unsafe.SizeOf<SdfLightGpu>()).IsEqualTo(48);
        await Assert.That(Unsafe.SizeOf<SdfFrameUniformsGpu>()).IsEqualTo(208 + SdfRenderer.MaxLights * 48);
        await Assert.That(Unsafe.SizeOf<SdfCompositeUniformsGpu>()).IsEqualTo(16);
    }

    [Test]
    public async Task scene_program_binds_frame_uniforms_and_a_read_only_shape_buffer()
    {
        var program = Load("Shaders.sdfScene");

        await Assert.That(program.Layout.Groups.Length).IsEqualTo(1);
        var entries = program.Layout.Groups[0].Entries;
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(entries[1].Type).IsEqualTo(BindingResourceType.ReadonlyStorageBuffer);
    }

    [Test]
    public async Task validator_catches_a_doctored_field_offset()
    {
        var program = Load("Shaders.sdfScene");
        var block = program.UniformBlocks[0];
        var fields = (UniformFieldDesc[])block.Fields.Clone();
        fields[2] = fields[2] with { Offset = fields[2].Offset + 4 };
        var doctored = new ShaderProgramDesc(program.Modules, program.Layout, program.VertexBuffers)
        {
            UniformBlocks = [block with { Fields = fields }],
        };

        await Assert.That(() => SdfUniformLayout.ValidateScene(doctored)).Throws<InvalidOperationException>();
    }
}

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr.Test;

/// <summary>The keystone suite: the CPU uniform mirrors must byte-match the layout the REAL
/// slangc reflected from pbr.slang at build time. Drift in the shader, the schema, or the
/// mirror structs breaks here — in CI, on the CPU — instead of corrupting a frame.</summary>
public class UniformLayoutTests
{
    private static ShaderProgramDesc LoadProgram() =>
        WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.pbr");

    [Test]
    public async Task mirrors_match_the_reflected_layout()
    {
        var program = LoadProgram();
        UniformLayoutValidator.Validate(program); // throws on any divergence
        await Assert.That(program.UniformBlocks.Length).IsEqualTo(4); // draw, frame, material, ssao
    }

    [Test]
    public async Task struct_sizes_match_wgsl_totals()
    {
        await Assert.That(Unsafe.SizeOf<DrawUniformsGpu>()).IsEqualTo(208);
        await Assert.That(Unsafe.SizeOf<FrameUniformsGpu>()).IsEqualTo(3808);
        await Assert.That(Unsafe.SizeOf<MaterialUniformsGpu>()).IsEqualTo(80);
        await Assert.That(Unsafe.SizeOf<SceneLightGpu>()).IsEqualTo(80);
    }

    [Test]
    public async Task validator_catches_a_doctored_field_offset()
    {
        var program = LoadProgram();
        UniformBlockDesc? material = null;
        foreach (var block in program.UniformBlocks)
        {
            if (block.Name == "material") material = block;
        }
        var doctoredFields = (UniformFieldDesc[])material!.Fields.Clone();
        doctoredFields[1] = doctoredFields[1] with { Offset = doctoredFields[1].Offset + 4 };
        var doctored = material with { Fields = doctoredFields };

        var blocks = new UniformBlockDesc[program.UniformBlocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = program.UniformBlocks[i].Name == "material" ? doctored : program.UniformBlocks[i];
        }
        var doctoredProgram = new ShaderProgramDesc(program.Modules, program.Layout, program.VertexBuffers)
        {
            UniformBlocks = blocks,
        };

        await Assert.That(() => UniformLayoutValidator.Validate(doctoredProgram))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task pipeline_layout_reflects_the_four_groups()
    {
        var program = LoadProgram();
        await Assert.That(program.Layout.Groups.Length).IsEqualTo(4);
        await Assert.That(program.Layout.Groups[0].Entries.Length).IsEqualTo(1); // draw UBO
        await Assert.That(program.Layout.Groups[1].Entries.Length).IsEqualTo(3); // frame UBO + shadow depth texture + comparison sampler
        await Assert.That(program.Layout.Groups[2].Entries.Length).IsEqualTo(7); // material UBO + 5 tex + sampler
        await Assert.That(program.Layout.Groups[3].Entries.Length).IsEqualTo(2); // SSAO UBO + position texture
    }

    [Test]
    public async Task program_authors_three_entry_points()
    {
        var program = LoadProgram();
        var names = new List<string>();
        foreach (var module in program.Modules) names.Add(module.EntryPoint);
        await Assert.That(names).Contains("vertexMain");
        await Assert.That(names).Contains("fragmentMain");
        await Assert.That(names).Contains("fragmentMainSrgb");
    }

    [Test]
    public async Task vertex_layout_is_the_twelve_float_gltf_interleave()
    {
        var program = LoadProgram();
        await Assert.That(program.VertexBuffers.Length).IsEqualTo(1);
        var vb = program.VertexBuffers[0];
        await Assert.That(vb.Stride).IsEqualTo((ulong)(GltfPrimitiveFloats * sizeof(float)));
        await Assert.That(vb.Attributes.Length).IsEqualTo(4);
        await Assert.That(vb.Attributes[0].Format).IsEqualTo(VertexFormat.Float32x3); // pos
        await Assert.That(vb.Attributes[1].Format).IsEqualTo(VertexFormat.Float32x3); // normal
        await Assert.That(vb.Attributes[2].Format).IsEqualTo(VertexFormat.Float32x2); // uv
        await Assert.That(vb.Attributes[3].Format).IsEqualTo(VertexFormat.Float32x4); // tangent
    }

    private const int GltfPrimitiveFloats = 12;

    [Test]
    public async Task matrix_bytes_carry_translation_at_flat_12_to_14()
    {
        // Pin the raw-bytes convention the whole uniform path rests on: System.Numerics
        // row-major storage puts translation at flat floats 12..14 — exactly where WGSL's
        // column-major mat4x4 expects the translation column for mul(M, v) math.
        var m = Matrix4x4.CreateTranslation(new Vector3(5f, 6f, 7f));
        var flat = new float[16]; // array, not stackalloc — Span<T> can't cross await (CS4007)
        MemoryMarshal.Write(MemoryMarshal.AsBytes(flat.AsSpan()), in m);
        await Assert.That(flat[12]).IsEqualTo(5f);
        await Assert.That(flat[13]).IsEqualTo(6f);
        await Assert.That(flat[14]).IsEqualTo(7f);
        await Assert.That(flat[15]).IsEqualTo(1f);
    }

    [Test]
    public async Task light_packing_golden()
    {
        var light = new PbrLight
        {
            Type = PbrLightType.Spot,
            Position = new Vector3(1f, 2f, 3f),
            Direction = new Vector3(0f, -1f, 0f),
            Color = new Vector3(0.5f, 0.25f, 0.125f),
            Intensity = 4f,
            Range = 12f,
            SpotOuterDegrees = 40f,
            SpotInnerDegrees = 25f,
        };
        var gpu = light.ToGpu();
        await Assert.That(gpu.PositionAndType).IsEqualTo(new Vector4(1f, 2f, 3f, 2f));
        await Assert.That(gpu.DirectionAndRange).IsEqualTo(new Vector4(0f, -1f, 0f, 12f));
        await Assert.That(gpu.ColorAndIntensity).IsEqualTo(new Vector4(0.5f, 0.25f, 0.125f, 4f));
        await Assert.That(gpu.SpotAngles).IsEqualTo(new Vector4(40f, 25f, -1f, 0f)); // z=-1 → no shadow (renderer assigns array layers)
    }
}

using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Test;

/// <summary>Checks that a CPU struct is compared with a reflected buffer by its own measured layout.</summary>
public class ShaderMirrorTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Mirror
    {
        public Matrix4x4 Transform;
        public Vector3 Origin;
        public uint Count;
#pragma warning disable CS0169, IDE0051 // Reserved lane the shader names and nothing reads.
        private uint _reserved;
#pragma warning restore CS0169, IDE0051
        public uint Flags;
    }

    /// <summary>The storage fixture's <c>Item</c>: 28 bytes of fields in a 32-byte stride.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct Item
    {
        public Vector3 Origin;
        public uint Count;
        public Vector2 Uv;
        public uint Flags;
    }

    private static readonly UniformFieldDesc[] s_fields =
    [
        new("transform", 0, 64),
        new("origin", 64, 12),
        new("count", 76, 4),
        new("reserved", 80, 4),
        new("flags", 84, 4),
    ];

    private static ShaderProgramDesc Program(UniformFieldDesc[] fields, uint size = 88) =>
        new([], new PipelineLayoutDesc([], []), []) { UniformBlocks = [new UniformBlockDesc("block", 0, 0, size, fields)] };

    private static void Check(ShaderProgramDesc program)
    {
        var mirror = default(Mirror);
        ShaderMirror.Uniform(program, "block", ref mirror)
            .Field("transform", ref mirror.Transform)
            .Field("origin", ref mirror.Origin)
            .Field("count", ref mirror.Count)
            .Padding("reserved")
            .Field("flags", ref mirror.Flags)
            .End();
    }

    [Test]
    public async Task a_struct_laid_out_as_the_shader_reflects_passes()
    {
        await Assert.That(() => Check(Program(s_fields))).ThrowsNothing();
    }

    [Test]
    public async Task a_moved_field_is_reported_with_both_layouts()
    {
        var moved = (UniformFieldDesc[])s_fields.Clone();
        moved[2] = moved[2] with { Offset = 80 };

        await Assert.That(() => Check(Program(moved))).Throws<InvalidOperationException>()
            .WithMessageContaining("holds 'count'@76+4 but the shader reflects 'count'@80+4");
    }

    [Test]
    public async Task a_renamed_field_is_reported()
    {
        var renamed = (UniformFieldDesc[])s_fields.Clone();
        renamed[4] = renamed[4] with { Name = "mask" };

        await Assert.That(() => Check(Program(renamed))).Throws<InvalidOperationException>().WithMessageContaining("'mask'@84+4");
    }

    [Test]
    public async Task padding_must_carry_the_shaders_name_and_follow_the_fields_before_it()
    {
        var renamed = (UniformFieldDesc[])s_fields.Clone();
        renamed[3] = renamed[3] with { Name = "spare" };
        var overlapping = (UniformFieldDesc[])s_fields.Clone();
        overlapping[3] = overlapping[3] with { Offset = 76 };

        await Assert.That(() => Check(Program(renamed))).Throws<InvalidOperationException>().WithMessageContaining("padding 'reserved'");
        await Assert.That(() => Check(Program(overlapping))).Throws<InvalidOperationException>().WithMessageContaining("from byte 80");
    }

    [Test]
    public async Task a_field_the_struct_does_not_name_fails_at_the_end()
    {
        UniformFieldDesc[] longer = [.. s_fields, new("extra", 88, 4)];

        await Assert.That(() => Check(Program(longer, size: 92))).Throws<InvalidOperationException>().WithMessageContaining("the next is 'extra'");
    }

    [Test]
    public async Task a_field_the_shader_does_not_reflect_fails_where_it_is_named()
    {
        await Assert.That(() => Check(Program(s_fields[..4]))).Throws<InvalidOperationException>()
            .WithMessageContaining("holds 'flags' as field 4 but the shader reflects 4 fields");
    }

    [Test]
    public async Task a_struct_of_another_size_fails_at_the_end()
    {
        await Assert.That(() => Check(Program(s_fields, size: 96))).Throws<InvalidOperationException>()
            .WithMessageContaining("is 88 bytes but the shader reflects 96");
    }

    [Test]
    public async Task a_reference_outside_the_struct_is_refused()
    {
        await Assert.That(() =>
        {
            var mirror = default(Mirror);
            var elsewhere = Matrix4x4.Identity;
            ShaderMirror.Uniform(Program(s_fields), "block", ref mirror).Field("transform", ref elsewhere);
        }).Throws<ArgumentException>();
    }

    [Test]
    public async Task an_absent_block_is_named()
    {
        await Assert.That(() =>
        {
            var mirror = default(Mirror);
            ShaderMirror.Uniform(Program(s_fields), "other", ref mirror);
        }).Throws<InvalidOperationException>().WithMessageContaining("'other'");
    }

    [Test]
    public async Task a_scalar_holding_the_first_lane_of_a_vector_counts_its_trailing_padding()
    {
        var program = Program([new("slot", 0, 16)], size: 16);

        await Assert.That(() =>
        {
            var slot = default(Vector4);
            ShaderMirror.Uniform(program, "block", ref slot).Field("slot", ref slot.X, trailingPadding: 12).End();
        }).ThrowsNothing();
        await Assert.That(() =>
        {
            var slot = default(Vector4);
            ShaderMirror.Uniform(program, "block", ref slot).Field("slot", ref slot.X);
        }).Throws<InvalidOperationException>().WithMessageContaining("'slot'@0+4");
    }

    [Test]
    public async Task structured_buffers_of_structs_reflect_their_fields_and_stride()
    {
        var program = ShaderProgramLoader.Load(typeof(ShaderMirrorTests).Assembly, "Shaders.storage");

        // A buffer of plain vectors has no fields to mirror.
        await Assert.That(program.StorageBlocks.Select(block => block.Name)).IsEquivalentTo(["items", "pairs"]);
        var items = program.StorageBlocks.Single(block => block.Name == "items");
        var pairs = program.StorageBlocks.Single(block => block.Name == "pairs");
        // 28 bytes of fields round up to the vec3's 16-byte alignment, 12 to the vec2's 8.
        await Assert.That(items.ElementStride).IsEqualTo(32u);
        await Assert.That(pairs.ElementStride).IsEqualTo(16u);
        await Assert.That(pairs.Binding).IsEqualTo(1u);
        await Assert.That(items.Fields[2]).IsEqualTo(new UniformFieldDesc("uv", 16, 8));

        await Assert.That(() =>
        {
            var item = default(Item);
            ShaderMirror.Storage(program, "items", ref item)
                .Field("origin", ref item.Origin)
                .Field("count", ref item.Count)
                .Field("uv", ref item.Uv)
                .Field("flags", ref item.Flags)
                .End();
        }).ThrowsNothing();
    }
}

using System;
using System.Buffers;

namespace Paradise.Rendering.Test;

public class RenderCommandEncoderTests
{
    [Test]
    public async Task encoder_round_trip_preserves_command_kinds_and_payloads()
    {
        var writer = new ArrayBufferWriter<RenderCommand>(8);
        var encoder = new RenderCommandEncoder(writer);

        var pipeline = new PipelineHandle(7, 3);
        var vbuf = new BufferHandle(11, 5);
        var ibuf = new BufferHandle(13, 5);
        var bindGroup = new BindGroupHandle(17, 2);

        encoder.BeginPass(0);
        encoder.SetPipeline(pipeline);
        encoder.SetVertexBuffer(slot: 1, vbuf, offset: 32, size: 256);
        encoder.SetIndexBuffer(ibuf, IndexFormat.Uint32, offset: 0, size: 64);
        encoder.SetBindGroup(2, bindGroup);
        encoder.Draw(new DrawCommand(VertexCount: 3, InstanceCount: 1, FirstVertex: 0, FirstInstance: 0));
        encoder.DrawIndexed(new DrawIndexedCommand(IndexCount: 6, InstanceCount: 1, FirstIndex: 0, BaseVertex: 0, FirstInstance: 0));
        encoder.EndPass();

        var memory = writer.WrittenMemory;
        await Assert.That(memory.Length).IsEqualTo(8);

        var commands = memory.ToArray();
        await Assert.That(commands[0].Kind).IsEqualTo(RenderCommandKind.BeginPass);
        await Assert.That(commands[0].BeginPass.PassIndex).IsEqualTo(0);

        await Assert.That(commands[1].Kind).IsEqualTo(RenderCommandKind.SetPipeline);
        await Assert.That(commands[1].SetPipeline.Pipeline).IsEqualTo(pipeline);

        await Assert.That(commands[2].Kind).IsEqualTo(RenderCommandKind.SetVertexBuffer);
        await Assert.That(commands[2].SetVertexBuffer.Slot).IsEqualTo(1u);
        await Assert.That(commands[2].SetVertexBuffer.Buffer).IsEqualTo(vbuf);
        await Assert.That(commands[2].SetVertexBuffer.Offset).IsEqualTo(32ul);
        await Assert.That(commands[2].SetVertexBuffer.Size).IsEqualTo(256ul);

        await Assert.That(commands[3].Kind).IsEqualTo(RenderCommandKind.SetIndexBuffer);
        await Assert.That(commands[3].SetIndexBuffer.Buffer).IsEqualTo(ibuf);
        await Assert.That(commands[3].SetIndexBuffer.Format).IsEqualTo(IndexFormat.Uint32);

        await Assert.That(commands[4].Kind).IsEqualTo(RenderCommandKind.SetBindGroup);
        await Assert.That(commands[4].SetBindGroup.GroupIndex).IsEqualTo(2u);
        await Assert.That(commands[4].SetBindGroup.BindGroup).IsEqualTo(bindGroup);
        await Assert.That(commands[4].SetBindGroup.DynamicOffsetsCount).IsEqualTo(0u);

        await Assert.That(commands[5].Kind).IsEqualTo(RenderCommandKind.Draw);
        await Assert.That(commands[5].Draw.VertexCount).IsEqualTo(3u);

        await Assert.That(commands[6].Kind).IsEqualTo(RenderCommandKind.DrawIndexed);
        await Assert.That(commands[6].DrawIndexed.IndexCount).IsEqualTo(6u);

        await Assert.That(commands[7].Kind).IsEqualTo(RenderCommandKind.EndPass);
    }

    [Test]
    public async Task encoder_throws_on_null_writer()
    {
        await Assert.That(() => new RenderCommandEncoder(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task set_bind_group_4arg_rejects_nonzero_dynamic_offsets_count()
    {
        // Iter-9 fix: M2 reserves bind-group dynamic offsets — BindGroupLayoutEntryDesc has no
        // HasDynamicOffset field so no public layout can satisfy the bind. Encoder must reject
        // count > 0 with a clear NotSupportedException at encode time.
        var writer = new ArrayBufferWriter<RenderCommand>(2);
        var bindGroup = new BindGroupHandle(1, 1);
        await Assert.That(() =>
        {
            var encoder = new RenderCommandEncoder(writer);
            encoder.SetBindGroup(0, bindGroup, dynamicOffsetsStart: 0, dynamicOffsetsCount: 1);
        }).Throws<NotSupportedException>();
    }

    [Test]
    public async Task set_bind_group_4arg_rejects_nonzero_start_when_count_is_zero()
    {
        // Iter-10 fix: when count == 0 the submit path never reads start, so accepting a
        // non-zero value would silently discard it. Encoder rejects with ArgumentException to
        // surface the API smell immediately rather than letting the unused value linger in the
        // payload. Use the parameterless overload for the no-offsets case.
        var writer = new ArrayBufferWriter<RenderCommand>(2);
        var bindGroup = new BindGroupHandle(1, 1);
        await Assert.That(() =>
        {
            var encoder = new RenderCommandEncoder(writer);
            encoder.SetBindGroup(0, bindGroup, dynamicOffsetsStart: 7, dynamicOffsetsCount: 0);
        }).Throws<ArgumentException>();
    }
}

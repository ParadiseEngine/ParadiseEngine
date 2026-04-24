using System;
using System.Buffers;

namespace Paradise.Rendering;

/// <summary>Append-only encoder for <see cref="RenderCommand"/>s. Writes through a caller-owned
/// <see cref="IBufferWriter{T}"/> so the buffer/pool strategy stays a host concern.</summary>
public ref struct RenderCommandEncoder
{
    private readonly IBufferWriter<RenderCommand> _writer;

    public RenderCommandEncoder(IBufferWriter<RenderCommand> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    private void Write(in RenderCommand cmd)
    {
        var span = _writer.GetSpan(1);
        span[0] = cmd;
        _writer.Advance(1);
    }

    public void BeginPass(int passIndex) =>
        Write(RenderCommand.FromBeginPass(passIndex));

    public void EndPass() =>
        Write(RenderCommand.FromEndPass());

    public void SetPipeline(PipelineHandle pipeline) =>
        Write(RenderCommand.FromSetPipeline(pipeline));

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, ulong size) =>
        Write(RenderCommand.FromSetVertexBuffer(slot, buffer, offset, size));

    public void SetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset, ulong size) =>
        Write(RenderCommand.FromSetIndexBuffer(buffer, format, offset, size));

    /// <summary>Encode a <see cref="RenderCommandKind.SetBindGroup"/> with no dynamic offsets.</summary>
    public void SetBindGroup(uint groupIndex, BindGroupHandle bindGroup) =>
        Write(RenderCommand.FromSetBindGroup(groupIndex, bindGroup, 0, 0));

    /// <summary>Encode a <see cref="RenderCommandKind.SetBindGroup"/> referencing dynamic offsets
    /// already written to the enclosing <see cref="RenderCommandStream.DynamicOffsets"/> buffer.
    /// Host code owns the offset buffer; this encoder only records the index range.</summary>
    public void SetBindGroup(uint groupIndex, BindGroupHandle bindGroup, uint dynamicOffsetsStart, uint dynamicOffsetsCount) =>
        Write(RenderCommand.FromSetBindGroup(groupIndex, bindGroup, dynamicOffsetsStart, dynamicOffsetsCount));

    public void Draw(in DrawCommand cmd) =>
        Write(RenderCommand.FromDraw(cmd));

    public void DrawIndexed(in DrawIndexedCommand cmd) =>
        Write(RenderCommand.FromDrawIndexed(cmd));
}

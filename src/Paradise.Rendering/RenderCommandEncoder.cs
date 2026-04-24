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

    public void SetBindGroup(uint groupIndex) =>
        Write(RenderCommand.FromSetBindGroup(groupIndex));

    public void Draw(in DrawCommand cmd) =>
        Write(RenderCommand.FromDraw(cmd));

    public void DrawIndexed(in DrawIndexedCommand cmd) =>
        Write(RenderCommand.FromDrawIndexed(cmd));
}

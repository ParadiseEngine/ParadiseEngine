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

    /// <summary>Bind a group in the CURRENT pass — render or compute; the payload is pass-kind
    /// agnostic and both backends route it to whichever pass is open.</summary>
    public void SetBindGroup(uint groupIndex, BindGroupHandle group) =>
        Write(RenderCommand.FromSetBindGroup(groupIndex, group));

    /// <summary>Bind with a dynamic byte offset — the group's layout must carry exactly one
    /// <c>HasDynamicOffset</c> buffer entry (the draw-UBO-ring pattern).</summary>
    public void SetBindGroup(uint groupIndex, BindGroupHandle group, uint dynamicOffset) =>
        Write(RenderCommand.FromSetBindGroup(groupIndex, group, dynamicOffset));

    public void Draw(in DrawCommand cmd) =>
        Write(RenderCommand.FromDraw(cmd));

    public void DrawIndexed(in DrawIndexedCommand cmd) =>
        Write(RenderCommand.FromDrawIndexed(cmd));

    /// <summary>Restrict rasterization to a pixel-space rectangle of the current pass's attachment
    /// (the shadow-atlas tile a light renders into). Depth range defaults to [0, 1].</summary>
    public void SetViewport(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f) =>
        Write(RenderCommand.FromSetViewport(x, y, width, height, minDepth, maxDepth));

    /// <summary>Open a compute pass. Compute passes have no attachments, so there is no pass-table
    /// entry — close with <see cref="EndComputePass"/>, never <see cref="EndPass"/>.</summary>
    public void BeginComputePass() =>
        Write(RenderCommand.FromBeginComputePass());

    public void EndComputePass() =>
        Write(RenderCommand.FromEndComputePass());

    public void SetComputePipeline(ComputePipelineHandle pipeline) =>
        Write(RenderCommand.FromSetComputePipeline(pipeline));

    public void Dispatch(in DispatchCommand cmd) =>
        Write(RenderCommand.FromDispatch(cmd));
}

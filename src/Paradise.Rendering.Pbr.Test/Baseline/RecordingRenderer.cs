using System;
using System.Collections.Generic;
using Paradise.Rendering;

namespace Paradise.Rendering.Pbr.Test.Baseline;

/// <summary>An <see cref="IRenderer"/> that forwards everything to a real backend and keeps a copy
/// of every <see cref="RenderCommandStream"/> submitted through it.
///
/// This exists because the pass table is what the frame-graph migration actually changes, and the
/// pass table is not observable from pixels: a pass that renders the right thing into the wrong
/// slot, or a load op that flips from Clear to Load, can leave a frame that looks identical on one
/// scene and wrong on the next. Recording the stream makes the structure assertable directly, and
/// unlike a pixel comparison the result does not depend on which GPU ran it.
///
/// A decorator rather than a hook on <c>PbrRenderer</c>: <c>PbrRenderer</c> already takes
/// <see cref="IRenderer"/>, so the baseline needs no production change at all.</summary>
internal sealed class RecordingRenderer : IRenderer
{
    private readonly IRenderer _inner;
    private readonly List<CapturedFrame> _frames = [];

    public RecordingRenderer(IRenderer inner) => _inner = inner;

    /// <summary>One submitted stream, deep-copied. The renderer reuses its command and pass arrays
    /// between frames, so holding the <see cref="ReadOnlyMemory{T}"/> would alias whatever the next
    /// frame writes.</summary>
    internal readonly record struct CapturedFrame(
        RenderCommand[] Commands,
        RenderPassDesc[] Passes,
        bool Presenting);

    internal IReadOnlyList<CapturedFrame> Frames => _frames;

    /// <summary>The last presenting submit — the frame a pixel readback corresponds to.</summary>
    internal CapturedFrame LastPresentedFrame
    {
        get
        {
            for (var i = _frames.Count - 1; i >= 0; i--)
                if (_frames[i].Presenting)
                    return _frames[i];
            throw new InvalidOperationException("No presenting frame was submitted.");
        }
    }

    internal void Clear() => _frames.Clear();

    private void Capture(in RenderCommandStream stream, bool presenting) =>
        _frames.Add(new CapturedFrame(stream.Commands.ToArray(), stream.Passes.ToArray(), presenting));

    public void Submit(in RenderCommandStream stream)
    {
        Capture(in stream, presenting: true);
        _inner.Submit(in stream);
    }

    public void SubmitOffscreen(in RenderCommandStream stream)
    {
        Capture(in stream, presenting: false);
        _inner.SubmitOffscreen(in stream);
    }

    // ---- pure delegation below ----------------------------------------------------------------

    public TextureFormat ColorFormat => _inner.ColorFormat;
    public bool SupportsBcTextureCompression => _inner.SupportsBcTextureCompression;
    public uint UniformBufferOffsetAlignment => _inner.UniformBufferOffsetAlignment;

    public void Resize(uint width, uint height) => _inner.Resize(width, height);

    public BufferHandle CreateBuffer(in BufferDesc desc) => _inner.CreateBuffer(in desc);

    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged =>
        _inner.CreateBufferWithData(in desc, data);

    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged =>
        _inner.UpdateBuffer(handle, offset, data);

    public void DestroyBuffer(BufferHandle handle) => _inner.DestroyBuffer(handle);

    public TextureHandle CreateTexture(in TextureDesc desc) => _inner.CreateTexture(in desc);

    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height) =>
        _inner.WriteTexture(handle, mipLevel, data, bytesPerRow, rowsPerImage, width, height);

    public void DestroyTexture(TextureHandle handle) => _inner.DestroyTexture(handle);

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc) => _inner.CreateTextureView(in desc);

    public void DestroyTextureView(TextureViewHandle handle) => _inner.DestroyTextureView(handle);

    public SamplerHandle CreateSampler(in SamplerDesc desc) => _inner.CreateSampler(in desc);

    public void DestroySampler(SamplerHandle handle) => _inner.DestroySampler(handle);

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc) => _inner.CreateBindGroup(in desc);

    public void DestroyBindGroup(BindGroupHandle handle) => _inner.DestroyBindGroup(handle);

    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program,
        TextureFormat colorFormat,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16,
        TextureFormat? depthStencilFormat = null,
        BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true,
        CompareFunction depthCompare = CompareFunction.Less,
        string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null) =>
        _inner.CreatePipeline(in program, colorFormat, topology, stripIndexFormat, depthStencilFormat,
            blend, depthWriteEnabled, depthCompare, fragmentEntryPoint, vertexEntryPoint);

    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program,
        TextureFormat depthStencilFormat,
        ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less,
        string? vertexEntryPoint = null) =>
        _inner.CreateDepthOnlyPipeline(in program, depthStencilFormat, vertexLayouts, depthCompare, vertexEntryPoint);

    public void DestroyPipeline(PipelineHandle handle) => _inner.DestroyPipeline(handle);

    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null) =>
        _inner.CreateComputePipeline(in program, entryPoint);

    public void DestroyComputePipeline(ComputePipelineHandle handle) => _inner.DestroyComputePipeline(handle);
}

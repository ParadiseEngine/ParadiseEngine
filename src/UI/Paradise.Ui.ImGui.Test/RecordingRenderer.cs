using System;
using System.Collections.Generic;
using Paradise.Rendering;

namespace Paradise.Ui.ImGui.Test;

/// <summary>An <see cref="IRenderer"/> with no GPU behind it: hands out handles, remembers what was
/// created, written and destroyed, and accepts any stream.</summary>
internal sealed class RecordingRenderer : IRenderer
{
    private uint _next;

    public readonly record struct RegionWrite(TextureHandle Texture, uint X, uint Y, uint Width, uint Height, uint BytesPerRow, int Bytes);

    public HashSet<TextureHandle> LiveTextures { get; } = [];
    public HashSet<BufferHandle> LiveBuffers { get; } = [];
    public Dictionary<BindGroupHandle, BindGroupDesc> LiveBindGroups { get; } = [];
    public HashSet<SamplerHandle> LiveSamplers { get; } = [];
    public HashSet<PipelineHandle> LivePipelines { get; } = [];
    public List<RegionWrite> Writes { get; } = [];
    public List<(PipelineHandle Pipeline, BlendMode Blend, TextureFormat Format)> Pipelines { get; } = [];

    public TextureFormat ColorFormat => TextureFormat.Bgra8Unorm;
    public TextureCompressionFormats SupportedTextureCompression => default;
    public uint UniformBufferOffsetAlignment => 256;

    public int LiveCount => LiveTextures.Count + LiveBuffers.Count + LiveBindGroups.Count + LiveSamplers.Count + LivePipelines.Count;

    private uint Next() => ++_next;

    public void Resize(uint width, uint height) { }

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        var handle = new BufferHandle(Next(), 1);
        LiveBuffers.Add(handle);
        return handle;
    }

    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged => CreateBuffer(desc);

    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!LiveBuffers.Contains(handle)) throw new InvalidOperationException("Update of a destroyed buffer.");
        if (System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).Length % 4 != 0)
            throw new InvalidOperationException("Buffer writes must be whole words.");
    }

    public void DestroyBuffer(BufferHandle handle) => LiveBuffers.Remove(handle);

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var handle = new SamplerHandle(Next(), 1);
        LiveSamplers.Add(handle);
        return handle;
    }

    public void DestroySampler(SamplerHandle handle) => LiveSamplers.Remove(handle);

    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program, TextureFormat colorFormat, PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16, TextureFormat? depthStencilFormat = null, BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true, CompareFunction depthCompare = CompareFunction.Less, string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null)
    {
        var handle = new PipelineHandle(Next(), 1);
        LivePipelines.Add(handle);
        Pipelines.Add((handle, blend, colorFormat));
        return handle;
    }

    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program, TextureFormat depthStencilFormat, ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less, string? vertexEntryPoint = null) => throw new NotSupportedException();

    public void DestroyPipeline(PipelineHandle handle) => LivePipelines.Remove(handle);

    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null) => throw new NotSupportedException();

    public void DestroyComputePipeline(ComputePipelineHandle handle) { }

    public void Submit(in RenderCommandStream stream) => RenderCommandStreamValidator.Validate(stream, allowBackbuffer: true);

    public void SubmitOffscreen(in RenderCommandStream stream) => RenderCommandStreamValidator.Validate(stream, allowBackbuffer: false);

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var handle = new TextureHandle(Next(), 1);
        LiveTextures.Add(handle);
        return handle;
    }

    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1)
        => WriteTextureRegion(handle, mipLevel, 0, 0, data, bytesPerRow, width, height);

    public void WriteTextureRegion(TextureHandle handle, uint mipLevel, uint originX, uint originY, ReadOnlySpan<byte> data, uint bytesPerRow, uint width, uint height)
    {
        if (!LiveTextures.Contains(handle)) throw new InvalidOperationException("Write to a destroyed texture.");
        Writes.Add(new RegionWrite(handle, originX, originY, width, height, bytesPerRow, data.Length));
    }

    public void DestroyTexture(TextureHandle handle) => LiveTextures.Remove(handle);

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc) => new(Next(), 1);

    public void DestroyTextureView(TextureViewHandle handle) { }

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        var handle = new BindGroupHandle(Next(), 1);
        LiveBindGroups.Add(handle, desc);
        return handle;
    }

    public void DestroyBindGroup(BindGroupHandle handle) => LiveBindGroups.Remove(handle);
}

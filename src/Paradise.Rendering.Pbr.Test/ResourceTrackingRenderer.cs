using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr.Test;

internal sealed class ResourceTrackingRenderer : IRenderer
{
    private uint _nextHandle = 1;
    public Dictionary<BufferHandle, BufferDesc> Buffers { get; } = [];
    public Dictionary<BufferHandle, byte[]> BufferData { get; } = [];
    public Dictionary<BufferHandle, int> DestroyedBuffers { get; } = [];
    public Dictionary<TextureHandle, TextureDesc> Textures { get; } = [];
    public Dictionary<TextureHandle, int> DestroyedTextures { get; } = [];
    public Dictionary<TextureViewHandle, TextureViewDesc> Views { get; } = [];
    public Dictionary<SamplerHandle, SamplerDesc> Samplers { get; } = [];
    public Dictionary<BindGroupHandle, BindGroupDesc> BindGroups { get; } = [];
    public Dictionary<BindGroupHandle, int> DestroyedBindGroups { get; } = [];
    public Dictionary<PipelineHandle, ShaderProgramDesc> Pipelines { get; } = [];
    public Dictionary<PipelineHandle, int> DestroyedPipelines { get; } = [];
    public Dictionary<ComputePipelineHandle, ShaderProgramDesc> ComputePipelines { get; } = [];
    public string? FailNextBufferName { get; set; }
    public bool FailNextBindGroup { get; set; }
    public bool FailNextPipeline { get; set; }
    public int? FailTextureWriteAfter { get; set; }
    public RenderCommand[] LastCommands { get; private set; } = [];

    public TextureFormat ColorFormat => TextureFormat.Rgba8Unorm;
    public bool SupportsBcTextureCompression => false;
    public uint UniformBufferOffsetAlignment => 256;
    public int ResourceCount => Buffers.Count + Textures.Count + Views.Count + Samplers.Count
        + BindGroups.Count + Pipelines.Count + ComputePipelines.Count;

    public void Resize(uint width, uint height) { }

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        if (FailNextBufferName is not null && FailNextBufferName == desc.Name)
        {
            FailNextBufferName = null;
            throw new InvalidOperationException("Injected buffer creation failure.");
        }
        var handle = new BufferHandle(_nextHandle++, 1);
        Buffers.Add(handle, desc);
        BufferData.Add(handle, new byte[checked((int)desc.Size)]);
        return handle;
    }

    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        var handle = CreateBuffer(desc with { Size = Math.Max(desc.Size, (ulong)bytes.Length) });
        bytes.CopyTo(BufferData[handle]);
        return handle;
    }

    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged =>
        MemoryMarshal.AsBytes(data).CopyTo(BufferData[handle].AsSpan(checked((int)offset)));

    public void DestroyBuffer(BufferHandle handle)
    {
        Remove(Buffers, handle);
        BufferData.Remove(handle);
        DestroyedBuffers[handle] = DestroyedBuffers.GetValueOrDefault(handle) + 1;
    }

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var handle = new TextureHandle(_nextHandle++, 1);
        Textures.Add(handle, desc);
        return handle;
    }

    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow,
        uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1)
    {
        if (!Textures.ContainsKey(handle)) throw new InvalidOperationException("Texture is not live.");
        if (FailTextureWriteAfter is not { } remaining) return;
        FailTextureWriteAfter = remaining > 0 ? remaining - 1 : null;
        if (remaining == 0) throw new InvalidOperationException("Injected texture upload failure.");
    }

    public void DestroyTexture(TextureHandle handle)
    {
        Remove(Textures, handle);
        DestroyedTextures[handle] = DestroyedTextures.GetValueOrDefault(handle) + 1;
    }

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        if (!Textures.ContainsKey(desc.Texture)) throw new InvalidOperationException("Texture is not live.");
        var handle = new TextureViewHandle(_nextHandle++, 1);
        Views.Add(handle, desc);
        return handle;
    }

    public void DestroyTextureView(TextureViewHandle handle) => Remove(Views, handle);

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var handle = new SamplerHandle(_nextHandle++, 1);
        Samplers.Add(handle, desc);
        return handle;
    }

    public void DestroySampler(SamplerHandle handle) => Remove(Samplers, handle);

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        if (FailNextBindGroup)
        {
            FailNextBindGroup = false;
            throw new InvalidOperationException("Injected bind group creation failure.");
        }
        var handle = new BindGroupHandle(_nextHandle++, 1);
        BindGroups.Add(handle, desc with { Entries = desc.Entries.ToArray() });
        return handle;
    }

    public void DestroyBindGroup(BindGroupHandle handle)
    {
        Remove(BindGroups, handle);
        DestroyedBindGroups[handle] = DestroyedBindGroups.GetValueOrDefault(handle) + 1;
    }

    public PipelineHandle CreatePipeline(in ShaderProgramDesc program, TextureFormat colorFormat,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList, IndexFormat stripIndexFormat = IndexFormat.Uint16,
        TextureFormat? depthStencilFormat = null, BlendMode blend = BlendMode.Opaque, bool depthWriteEnabled = true,
        CompareFunction depthCompare = CompareFunction.Less, string? fragmentEntryPoint = null, string? vertexEntryPoint = null)
    {
        if (FailNextPipeline)
        {
            FailNextPipeline = false;
            throw new InvalidOperationException("Injected pipeline creation failure.");
        }
        var handle = new PipelineHandle(_nextHandle++, 1);
        Pipelines.Add(handle, program);
        return handle;
    }

    public PipelineHandle CreateDepthOnlyPipeline(in ShaderProgramDesc program, TextureFormat depthStencilFormat,
        ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts, CompareFunction depthCompare = CompareFunction.Less,
        string? vertexEntryPoint = null) => CreatePipeline(program, ColorFormat);

    public void DestroyPipeline(PipelineHandle handle)
    {
        Remove(Pipelines, handle);
        DestroyedPipelines[handle] = DestroyedPipelines.GetValueOrDefault(handle) + 1;
    }

    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null)
    {
        var handle = new ComputePipelineHandle(_nextHandle++, 1);
        ComputePipelines.Add(handle, program);
        return handle;
    }

    public void DestroyComputePipeline(ComputePipelineHandle handle) => Remove(ComputePipelines, handle);

    public void Submit(in RenderCommandStream stream) => LastCommands = stream.Commands.ToArray();
    public void SubmitOffscreen(in RenderCommandStream stream) => LastCommands = stream.Commands.ToArray();

    private static void Remove<THandle, TDesc>(Dictionary<THandle, TDesc> resources, THandle handle)
        where THandle : notnull
    {
        if (!resources.Remove(handle)) throw new InvalidOperationException($"Resource {handle} is not live.");
    }
}

using Paradise.Rendering;

namespace Paradise.Runtime.Test;

/// <summary>An <see cref="IRenderer"/> with no GPU: hands out handles, tracks what is live and keeps the last
/// submitted stream, so the reference host composes on any CI lane.</summary>
internal sealed class RecordingBackend : IRenderer
{
    private uint _next;

    public HashSet<BufferHandle> Buffers { get; } = [];
    public HashSet<TextureHandle> Textures { get; } = [];
    public List<(TextureHandle Texture, TextureFormat Format)> CreatedTextures { get; } = [];
    public int Submits { get; private set; }
    public RenderCommand[] LastCommands { get; private set; } = [];

    public TextureFormat ColorFormat => TextureFormat.Bgra8Unorm;
    public TextureCompressionFormats SupportedTextureCompression => default;
    public uint UniformBufferOffsetAlignment => 256;

    private uint Next() => ++_next;

    public void Resize(uint width, uint height) { }

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        var handle = new BufferHandle(Next(), 1);
        Buffers.Add(handle);
        return handle;
    }

    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged => CreateBuffer(desc);
    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged { }
    public void DestroyBuffer(BufferHandle handle) => Buffers.Remove(handle);
    public SamplerHandle CreateSampler(in SamplerDesc desc) => new(Next(), 1);
    public void DestroySampler(SamplerHandle handle) { }

    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program, TextureFormat colorFormat, PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16, TextureFormat? depthStencilFormat = null, BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true, CompareFunction depthCompare = CompareFunction.Less, string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null) => new(Next(), 1);

    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program, TextureFormat depthStencilFormat, ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less, string? vertexEntryPoint = null) => new(Next(), 1);

    public void DestroyPipeline(PipelineHandle handle) { }
    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null) => new(Next(), 1);
    public void DestroyComputePipeline(ComputePipelineHandle handle) { }

    public void Submit(in RenderCommandStream stream)
    {
        RenderCommandStreamValidator.Validate(stream, allowBackbuffer: true);
        LastCommands = stream.Commands.ToArray();
        Submits++;
    }

    public void SubmitOffscreen(in RenderCommandStream stream) => RenderCommandStreamValidator.Validate(stream, allowBackbuffer: false);

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var handle = new TextureHandle(Next(), 1);
        Textures.Add(handle);
        CreatedTextures.Add((handle, desc.Format));
        return handle;
    }

    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1) { }
    public void DestroyTexture(TextureHandle handle) => Textures.Remove(handle);
    public TextureViewHandle CreateTextureView(in TextureViewDesc desc) => new(Next(), 1);
    public void DestroyTextureView(TextureViewHandle handle) { }
    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc) => new(Next(), 1);
    public void DestroyBindGroup(BindGroupHandle handle) { }
}

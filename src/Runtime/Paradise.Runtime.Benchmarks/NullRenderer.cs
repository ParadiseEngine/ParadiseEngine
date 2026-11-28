using Paradise.Rendering;

namespace Paradise.Runtime.Benchmarks;

/// <summary>An <see cref="IRenderer"/> that hands out handles and drops every upload and stream, so a run
/// measures the engine's side of a frame and none of a backend's.</summary>
internal sealed class NullRenderer : IRenderer
{
    private uint _next;

    public TextureFormat ColorFormat => TextureFormat.Bgra8Unorm;
    public TextureCompressionFormats SupportedTextureCompression => default;
    public uint UniformBufferOffsetAlignment => 256;

    /// <summary>Commands in the last presented stream, so the frame is observably not empty.</summary>
    public int LastCommandCount { get; private set; }

    private uint Next() => ++_next;

    public void Resize(uint width, uint height) { }
    public BufferHandle CreateBuffer(in BufferDesc desc) => new(Next(), 1);
    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged => CreateBuffer(desc);
    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged { }
    public void DestroyBuffer(BufferHandle handle) { }
    public SamplerHandle CreateSampler(in SamplerDesc desc) => new(Next(), 1);
    public void DestroySampler(SamplerHandle handle) { }

    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program, TextureFormat colorFormat, PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16, TextureFormat? depthStencilFormat = null, BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true, CompareFunction depthCompare = CompareFunction.Less, string? fragmentEntryPoint = null,
        string? vertexEntryPoint = null, CullMode cullMode = CullMode.None) => new(Next(), 1);

    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program, TextureFormat depthStencilFormat, ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less, string? vertexEntryPoint = null, CullMode cullMode = CullMode.None) => new(Next(), 1);

    public void DestroyPipeline(PipelineHandle handle) { }
    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null) => new(Next(), 1);
    public void DestroyComputePipeline(ComputePipelineHandle handle) { }
    public void Submit(in RenderCommandStream stream) => LastCommandCount = stream.Commands.Length;
    public void SubmitOffscreen(in RenderCommandStream stream) { }
    public TextureHandle CreateTexture(in TextureDesc desc) => new(Next(), 1);
    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height, uint depthOrArrayLayers = 1) { }
    public void DestroyTexture(TextureHandle handle) { }
    public TextureViewHandle CreateTextureView(in TextureViewDesc desc) => new(Next(), 1);
    public void DestroyTextureView(TextureViewHandle handle) { }
    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc) => new(Next(), 1);
    public void DestroyBindGroup(BindGroupHandle handle) { }
}

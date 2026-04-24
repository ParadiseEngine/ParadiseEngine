using System;
using System.Buffers;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>Per-frame draw plumbing for the M2 textured indexed quad. Loads the embedded Slang
/// outputs, builds a pipeline whose vertex layout + bind group layouts come from the reflection
/// record (NOT hand-coded), uploads vertex + index buffers once, procedurally generates a 2x2
/// RGBA8 checkerboard texture, and updates a per-frame uniform to scroll the UV. The pipeline
/// includes a <see cref="TextureFormat.Depth32Float"/> depth attachment that writes every frame
/// to prove the depth path is live even with a single draw.</summary>
internal sealed class MaterialScene : IDisposable
{
    private const TextureFormat DepthFormat = TextureFormat.Depth32Float;

    // Quad vertices (position xy + uv) — four corners of the NDC quad. Indices produce two
    // triangles (0,1,2), (0,2,3) in triangle-list order.
    private static readonly float[] s_vertices =
    {
        // pos.x, pos.y, uv.x, uv.y
        -0.6f, -0.6f,  0.0f, 1.0f,  // bottom-left
         0.6f, -0.6f,  1.0f, 1.0f,  // bottom-right
         0.6f,  0.6f,  1.0f, 0.0f,  // top-right
        -0.6f,  0.6f,  0.0f, 0.0f,  // top-left
    };

    private static readonly ushort[] s_indices = { 0, 1, 2, 0, 2, 3 };

    // 2x2 RGBA8 checkerboard — alternating cornflower blue / near-white so UV scrolling is visible.
    private static readonly byte[] s_checkerboard =
    {
        100, 149, 237, 255,   240, 240, 240, 255,
        240, 240, 240, 255,   100, 149, 237, 255,
    };

    private readonly WebGpuRenderer _renderer;
    private readonly BufferHandle _vertexBuffer;
    private readonly BufferHandle _indexBuffer;
    private readonly BufferHandle _uniformBuffer;
    private readonly TextureHandle _albedoTexture;
    private readonly RenderViewHandle _albedoView;
    private readonly SamplerHandle _albedoSampler;
    private readonly TextureHandle _depthTexture;
    private readonly BindGroupLayoutHandle _frameLayout;
    private readonly BindGroupLayoutHandle _materialLayout;
    private readonly BindGroupHandle _frameBindGroup;
    private readonly BindGroupHandle _materialBindGroup;
    private readonly PipelineHandle _pipeline;
    private readonly RenderPassDesc[] _passes;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(16);
    private readonly uint _depthWidth;
    private readonly uint _depthHeight;
    private float _timeSeconds;

    public MaterialScene(WebGpuRenderer renderer, uint width, uint height)
    {
        _renderer = renderer;
        _depthWidth = width;
        _depthHeight = height;

        var program = WebGpuRenderer.LoadShaderProgram(typeof(MaterialScene).Assembly, "Shaders.material");
        if (program.Layout.Groups.Length != 2)
            throw new InvalidOperationException(
                $"material.reflection.json produced {program.Layout.Groups.Length} bind groups; expected 2 (frame + material). " +
                "Shader authoring / slangc version drift?");

        // Resolve reflection groups by GroupIndex (space 0 / 1). Relying on array order is fragile —
        // sort or lookup by GroupIndex so a reordered reflection doesn't silently mismatch.
        BindGroupLayoutDesc frameGroup = default!;
        BindGroupLayoutDesc materialGroup = default!;
        foreach (var g in program.Layout.Groups)
        {
            if (g.GroupIndex == 0) frameGroup = g;
            else if (g.GroupIndex == 1) materialGroup = g;
        }
        if (frameGroup is null || materialGroup is null)
            throw new InvalidOperationException("material.reflection.json must declare group 0 (frame) and group 1 (material).");

        _frameLayout = renderer.CreateBindGroupLayout(frameGroup);
        _materialLayout = renderer.CreateBindGroupLayout(materialGroup);

        var vertexBytes = (ulong)(s_vertices.Length * sizeof(float));
        _vertexBuffer = renderer.CreateBufferWithData(
            new BufferDesc("QuadVertices", vertexBytes, BufferUsage.Vertex),
            (ReadOnlySpan<float>)s_vertices);

        var indexBytes = (ulong)(s_indices.Length * sizeof(ushort));
        _indexBuffer = renderer.CreateBufferWithData(
            new BufferDesc("QuadIndices", indexBytes, BufferUsage.Index),
            (ReadOnlySpan<ushort>)s_indices);

        // Frame-uniform buffer: std140-padded size is 16 bytes (one float + 12 bytes padding).
        // Uniform+CopyDst so Queue.WriteBuffer can refresh the time every frame.
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc(
            Name: "FrameUniforms",
            Size: 16,
            Usage: BufferUsage.Uniform | BufferUsage.CopyDst));

        var texDesc = new TextureDesc(
            Name: "CheckerboardAlbedo",
            Width: 2,
            Height: 2,
            DepthOrArrayLayers: 1,
            MipLevelCount: 1,
            SampleCount: 1,
            Dimension: TextureDimension.D2,
            Format: TextureFormat.Rgba8Unorm,
            Usage: TextureUsage.TextureBinding);
        _albedoTexture = renderer.CreateTextureWithData(in texDesc, (ReadOnlySpan<byte>)s_checkerboard);
        _albedoView = renderer.CreateTextureView(_albedoTexture, new RenderViewDesc(
            Name: "CheckerboardView",
            Format: TextureFormat.Rgba8Unorm,
            Dimension: TextureViewDimension.D2,
            Aspect: TextureAspect.All,
            BaseMipLevel: 0,
            MipLevelCount: 1,
            BaseArrayLayer: 0,
            ArrayLayerCount: 1));

        _albedoSampler = renderer.CreateSampler(new SamplerDesc(
            Name: "LinearClampSampler",
            AddressU: SamplerAddressMode.ClampToEdge,
            AddressV: SamplerAddressMode.ClampToEdge,
            AddressW: SamplerAddressMode.ClampToEdge,
            MagFilter: SamplerFilterMode.Linear,
            MinFilter: SamplerFilterMode.Linear,
            MipmapFilter: SamplerFilterMode.Linear));

        var frameBuffers = new[] { new BindGroupBufferEntry(0, _uniformBuffer, 0, 0) };
        _frameBindGroup = renderer.CreateBindGroup(new BindGroupDesc
        {
            Name = "FrameBindGroup",
            Layout = _frameLayout,
            Buffers = frameBuffers,
        });

        var matTextures = new[] { new BindGroupTextureEntry(0, _albedoView) };
        var matSamplers = new[] { new BindGroupSamplerEntry(1, _albedoSampler) };
        _materialBindGroup = renderer.CreateBindGroup(new BindGroupDesc
        {
            Name = "MaterialBindGroup",
            Layout = _materialLayout,
            Textures = matTextures,
            Samplers = matSamplers,
        });

        _depthTexture = renderer.CreateTexture(new TextureDesc(
            Name: "SceneDepth",
            Width: width,
            Height: height,
            DepthOrArrayLayers: 1,
            MipLevelCount: 1,
            SampleCount: 1,
            Dimension: TextureDimension.D2,
            Format: DepthFormat,
            Usage: TextureUsage.RenderAttachment));

        var pipelineDesc = new PipelineDesc
        {
            Name = "TexturedQuadPipeline",
            VertexShader = default,
            VertexEntryPoint = string.Empty,
            FragmentShader = default,
            FragmentEntryPoint = string.Empty,
            VertexLayouts = program.VertexBuffers,
            Topology = PrimitiveTopology.TriangleList,
            StripIndexFormat = IndexFormat.Uint16,
            ColorFormat = renderer.ColorFormat,
            DepthStencilFormat = DepthFormat,
            Layout = program.Layout,
            BindGroupLayouts = new[] { _frameLayout, _materialLayout },
            DepthStencil = new DepthStencilState(
                Format: DepthFormat,
                DepthWriteEnabled: true,
                DepthCompare: CompareFunction.Less),
        };
        _pipeline = renderer.CreatePipeline(program, pipelineDesc);

        _passes = new RenderPassDesc[1];
        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1, depth: new DepthAttachmentDesc(
            DepthTexture: _depthTexture,
            DepthLoad: LoadOp.Clear,
            DepthStore: StoreOp.Store,
            ClearDepth: 1.0f));
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            View: RenderViewHandle.Invalid,
            Load: LoadOp.Clear,
            Store: StoreOp.Store,
            ClearValue: ColorRgba.CornflowerBlue);
    }

    public void Resize(uint width, uint height)
    {
        if (width == 0 || height == 0) return;
        if (width == _depthWidth && height == _depthHeight) return;
        // Depth texture recreate on resize would be a natural M2 follow-up; keeping current texture
        // means the depth attachment silently mismatches the color attachment after resize. Log and
        // skip here; M3 (or whichever milestone owns window-resize-safe depth) handles this.
        Console.Error.WriteLine(
            $"[MaterialScene] Resize to {width}x{height} requested; depth texture stays at {_depthWidth}x{_depthHeight}. " +
            "Resize-safe depth is a follow-up milestone.");
    }

    public void RenderFrame(float deltaTimeSeconds)
    {
        _timeSeconds += deltaTimeSeconds;

        // Refresh the per-frame uniform. A single float in a 16-byte aligned buffer; the padding
        // after it is untouched by the shader so we only write 4 bytes.
        Span<float> uniform = stackalloc float[1] { _timeSeconds };
        _renderer.WriteBuffer(_uniformBuffer, 0, (ReadOnlySpan<float>)uniform);

        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);
        encoder.BeginPass(0);
        encoder.SetPipeline(_pipeline);
        encoder.SetBindGroup(0, _frameBindGroup);
        encoder.SetBindGroup(1, _materialBindGroup);
        encoder.SetVertexBuffer(0, _vertexBuffer, 0, (ulong)(s_vertices.Length * sizeof(float)));
        encoder.SetIndexBuffer(_indexBuffer, IndexFormat.Uint16, 0, (ulong)(s_indices.Length * sizeof(ushort)));
        encoder.DrawIndexed(new DrawIndexedCommand(
            IndexCount: (uint)s_indices.Length,
            InstanceCount: 1,
            FirstIndex: 0,
            BaseVertex: 0,
            FirstInstance: 0));
        encoder.EndPass();

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes);
        _renderer.Submit(in stream);
    }

    public void Dispose()
    {
        _renderer.DestroyPipeline(_pipeline);
        _renderer.DestroyBindGroup(_materialBindGroup);
        _renderer.DestroyBindGroup(_frameBindGroup);
        _renderer.DestroyBindGroupLayout(_materialLayout);
        _renderer.DestroyBindGroupLayout(_frameLayout);
        _renderer.DestroySampler(_albedoSampler);
        _renderer.DestroyTextureView(_albedoView);
        _renderer.DestroyTexture(_albedoTexture);
        _renderer.DestroyTexture(_depthTexture);
        _renderer.DestroyBuffer(_uniformBuffer);
        _renderer.DestroyBuffer(_indexBuffer);
        _renderer.DestroyBuffer(_vertexBuffer);
    }
}

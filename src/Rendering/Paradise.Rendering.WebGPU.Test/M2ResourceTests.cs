using System;
using System.Buffers;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>End-to-end coverage for the M2 resource-binding pipeline: textures, samplers,
/// uniform buffers, bind groups (static + dynamic offset), depth — all driven through the real
/// <c>bindings.slang</c> program (compiled by slangc at build time) against a headless device.
/// GPU tests skip (not fail) when no adapter is available.</summary>
public class M2ResourceTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint width = 32, uint height = 32)
    {
        try
        {
            return WebGpuRenderer.CreateHeadless(width, height);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return null;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return null;
        }
    }

    private static ShaderProgramDesc LoadBindingsProgram() =>
        WebGpuRenderer.LoadShaderProgram(typeof(M2ResourceTests).Assembly, "Shaders.bindings");

    private static TextureDesc SmallRgbaTexture(string name, uint size = 4, uint mips = 1) => new(
        Name: name,
        Width: size, Height: size, DepthOrArrayLayers: 1,
        MipLevelCount: mips, SampleCount: 1,
        Dimension: TextureDimension.D2,
        Format: TextureFormat.Rgba8Unorm,
        Usage: TextureUsage.TextureBinding | TextureUsage.CopyDst);

    private static SamplerDesc LinearRepeatSampler(string name) => new(
        Name: name,
        AddressU: SamplerAddressMode.Repeat,
        AddressV: SamplerAddressMode.Repeat,
        AddressW: SamplerAddressMode.Repeat,
        MagFilter: SamplerFilterMode.Linear,
        MinFilter: SamplerFilterMode.Linear,
        MipmapFilter: SamplerFilterMode.Linear);

    [Test]
    public async Task destroyed_texture_handle_is_stale_synchronously()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var desc = SmallRgbaTexture("stale-texture");
            var h = renderer.CreateTexture(in desc);
            var pixels = new byte[4 * 4 * 4];
            renderer.WriteTexture(h, 0, pixels, bytesPerRow: 16, rowsPerImage: 4, width: 4, height: 4);

            renderer.DestroyTexture(h);
            await Assert.That(() => renderer.WriteTexture(h, 0, pixels, 16, 4, 4, 4))
                .Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroyed_sampler_handle_is_stale_synchronously()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var desc = LinearRepeatSampler("stale-sampler");
            var h = renderer.CreateSampler(in desc);
            renderer.DestroySampler(h);

            // Referencing the destroyed sampler in a bind group must fail at create time.
            var program = LoadBindingsProgram();
            var frameSize = program.UniformBlocks[1].SizeBytes;
            var bufferDesc = new BufferDesc("frame-ubo", frameSize, BufferUsage.Uniform | BufferUsage.CopyDst);
            var frameBuffer = renderer.CreateBuffer(in bufferDesc);
            var texDesc = SmallRgbaTexture("sampler-probe-tex");
            var texture = renderer.CreateTexture(in texDesc);

            var groupDesc = new BindGroupDesc(
                "stale-sampler-group",
                program.Layout.Groups[1],
                new[]
                {
                    BindGroupEntryDesc.ForBuffer(0, frameBuffer, 0, frameSize),
                    BindGroupEntryDesc.ForTexture(1, texture),
                    BindGroupEntryDesc.ForSampler(2, h),
                });
            await Assert.That(() => renderer.CreateBindGroup(in groupDesc))
                .Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroyed_bind_group_handle_is_stale_at_submit()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var (pipeline, drawGroup, frameGroup, vertexBuffer, depth) = BuildBindingsScene(renderer);
            renderer.DestroyBindGroup(drawGroup);

            var stream = EncodeBindingsFrame(pipeline, drawGroup, frameGroup, vertexBuffer, depth, dynamicOffset: null);
            await Assert.That(() => renderer.Submit(in stream)).Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task bindings_program_renders_frames_with_uniforms_texture_sampler_and_depth()
    {
        // The M2 keystone smoke: real slangc-compiled program with two UBOs (one per group),
        // a sampled texture, a sampler, and a Depth32Float attachment — three frames headless.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var (pipeline, drawGroup, frameGroup, vertexBuffer, depth) = BuildBindingsScene(renderer);
            for (var i = 0; i < 3; i++)
            {
                var stream = EncodeBindingsFrame(pipeline, drawGroup, frameGroup, vertexBuffer, depth, dynamicOffset: null);
                renderer.Submit(in stream);
            }
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task dynamic_offset_bind_group_renders_two_draws_from_one_ring_buffer()
    {
        // The draw-UBO-ring pattern: one uniform buffer holding two 256-byte-aligned draw
        // blocks, one bind group with HasDynamicOffset, two draws selecting their block via
        // the SetBindGroup dynamic offset.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadBindingsProgram();
            var stride = renderer.UniformBufferOffsetAlignment;
            await Assert.That(stride >= 256u).IsTrue();

            var drawBlock = program.UniformBlocks[0];
            var ringDesc = new BufferDesc("draw-ring", stride * 2, BufferUsage.Uniform | BufferUsage.CopyDst);
            var ring = renderer.CreateBuffer(in ringDesc);
            var block = new byte[drawBlock.SizeBytes];
            renderer.UpdateBuffer<byte>(ring, 0, block);
            renderer.UpdateBuffer<byte>(ring, stride, block);

            // Rebuild group 0's layout with the dynamic-offset flag — a LAYOUT property, so the
            // pipeline must be built from the same modified layout to stay compatible.
            var g0 = program.Layout.Groups[0];
            var dynamicG0 = new BindGroupLayoutDesc(g0.GroupIndex, new[]
            {
                g0.Entries[0] with { HasDynamicOffset = true },
            });
            var dynamicLayout = new PipelineLayoutDesc(
                new[] { dynamicG0, program.Layout.Groups[1] },
                Array.Empty<PushConstantRangeDesc>());
            var dynamicProgram = new ShaderProgramDesc(program.Modules, dynamicLayout, program.VertexBuffers)
            {
                UniformBlocks = program.UniformBlocks,
            };

            var pipeline = renderer.CreatePipeline(dynamicProgram, renderer.ColorFormat);

            var drawGroupDesc = new BindGroupDesc("draw-ring-group", dynamicG0, new[]
            {
                BindGroupEntryDesc.ForBuffer(0, ring, 0, drawBlock.SizeBytes),
            });
            var drawGroup = renderer.CreateBindGroup(in drawGroupDesc);

            var (frameGroup, vertexBuffer) = BuildFrameGroupAndVertices(renderer, program);

            var writer = new ArrayBufferWriter<RenderCommand>(16);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(pipeline);
            encoder.SetBindGroup(1, frameGroup);
            encoder.SetVertexBuffer(0, vertexBuffer, 0, 3 * 20);
            encoder.SetBindGroup(0, drawGroup, dynamicOffset: 0);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.SetBindGroup(0, drawGroup, dynamicOffset: stride);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndPass();

            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);

            var stream = new RenderCommandStream(writer.WrittenMemory, passes);
            renderer.Submit(in stream); // must not throw
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task pipeline_and_pass_depth_mismatch_throws_synchronously_both_ways()
    {
        // Review follow-up on this PR: pipeline↔pass depth incompatibility used to surface only
        // as an async Dawn validation error via the uncaptured-error callback. Submit now
        // throws a descriptive InvalidOperationException at SetPipeline time, both directions.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadBindingsProgram();
            var (frameGroup, vertexBuffer) = BuildFrameGroupAndVertices(renderer, program);
            _ = frameGroup;
            _ = vertexBuffer;

            // Depth pipeline into a depth-less pass.
            var depthPipeline = renderer.CreatePipeline(
                program, renderer.ColorFormat, depthStencilFormat: TextureFormat.Depth32Float);
            var writer = new ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(depthPipeline);
            encoder.EndPass();
            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);
            await Assert.That(() => renderer.Submit(in stream)).Throws<InvalidOperationException>();

            // Depth-less pipeline into a depth pass.
            var flatPipeline = renderer.CreatePipeline(program, renderer.ColorFormat);
            var depthDesc = new TextureDesc(
                "mismatch-depth", 32, 32, 1, 1, 1,
                TextureDimension.D2, TextureFormat.Depth32Float, TextureUsage.RenderAttachment);
            var depth = renderer.CreateTexture(in depthDesc);
            var writer2 = new ArrayBufferWriter<RenderCommand>(4);
            var encoder2 = new RenderCommandEncoder(writer2);
            encoder2.BeginPass(0);
            encoder2.SetPipeline(flatPipeline);
            encoder2.EndPass();
            var passes2 = new RenderPassDesc[1];
            passes2[0] = new RenderPassDesc(colorAttachmentCount: 1)
            {
                Depth = new DepthAttachmentDesc(depth, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
            };
            passes2[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
            var stream2 = new RenderCommandStream(writer2.WrittenMemory, passes2);
            await Assert.That(() => renderer.Submit(in stream2)).Throws<InvalidOperationException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task bc_support_flag_is_readable_and_gates_bc_texture_creation()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var bcDesc = new TextureDesc(
                Name: "bc-probe",
                Width: 4, Height: 4, DepthOrArrayLayers: 1,
                MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Bc7RgbaUnormSrgb,
                Usage: TextureUsage.TextureBinding | TextureUsage.CopyDst);

            if (renderer.SupportsBcTextureCompression)
            {
                var h = renderer.CreateTexture(in bcDesc);
                await Assert.That(h.IsValid).IsTrue();
                renderer.DestroyTexture(h);
            }
            else
            {
                await Assert.That(() => renderer.CreateTexture(in bcDesc)).Throws<NotSupportedException>();
            }
        }
        finally
        {
            renderer.Dispose();
        }
    }

    // -------- helpers --------

    private static (BindGroupHandle FrameGroup, BufferHandle VertexBuffer) BuildFrameGroupAndVertices(
        WebGpuRenderer renderer, ShaderProgramDesc program)
    {
        var frameBlock = program.UniformBlocks[1];
        var frameDesc = new BufferDesc("frame-ubo", frameBlock.SizeBytes, BufferUsage.Uniform | BufferUsage.CopyDst);
        var frameBuffer = renderer.CreateBuffer(in frameDesc);
        renderer.UpdateBuffer<byte>(frameBuffer, 0, new byte[frameBlock.SizeBytes]);

        var texDesc = SmallRgbaTexture("frame-tex");
        var texture = renderer.CreateTexture(in texDesc);
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 255; pixels[i + 3] = 255; }
        renderer.WriteTexture(texture, 0, pixels, 16, 4, 4, 4);

        var samplerDesc = LinearRepeatSampler("frame-sampler");
        var sampler = renderer.CreateSampler(in samplerDesc);

        var frameGroupDesc = new BindGroupDesc("frame-group", program.Layout.Groups[1], new[]
        {
            BindGroupEntryDesc.ForBuffer(0, frameBuffer, 0, frameBlock.SizeBytes),
            BindGroupEntryDesc.ForTexture(1, texture),
            BindGroupEntryDesc.ForSampler(2, sampler),
        });
        var frameGroup = renderer.CreateBindGroup(in frameGroupDesc);

        // bindings.slang VsIn: float3 pos + float2 uv = 20-byte stride, one triangle.
        ReadOnlySpan<float> vertices =
        [
            -0.5f, -0.5f, 0.5f, 0f, 0f,
            0.5f, -0.5f, 0.5f, 1f, 0f,
            0f, 0.5f, 0.5f, 0.5f, 1f,
        ];
        var vbDesc = new BufferDesc("bindings-vb", 0, BufferUsage.Vertex);
        var vertexBuffer = renderer.CreateBufferWithData(in vbDesc, vertices);

        return (frameGroup, vertexBuffer);
    }

    private static (PipelineHandle Pipeline, BindGroupHandle DrawGroup, BindGroupHandle FrameGroup, BufferHandle VertexBuffer, TextureHandle Depth)
        BuildBindingsScene(WebGpuRenderer renderer)
    {
        var program = LoadBindingsProgram();
        var pipeline = renderer.CreatePipeline(
            program, renderer.ColorFormat, depthStencilFormat: TextureFormat.Depth32Float);

        var drawBlock = program.UniformBlocks[0];
        var drawDesc = new BufferDesc("draw-ubo", drawBlock.SizeBytes, BufferUsage.Uniform | BufferUsage.CopyDst);
        var drawBuffer = renderer.CreateBuffer(in drawDesc);
        renderer.UpdateBuffer<byte>(drawBuffer, 0, new byte[drawBlock.SizeBytes]);

        var drawGroupDesc = new BindGroupDesc("draw-group", program.Layout.Groups[0], new[]
        {
            BindGroupEntryDesc.ForBuffer(0, drawBuffer, 0, drawBlock.SizeBytes),
        });
        var drawGroup = renderer.CreateBindGroup(in drawGroupDesc);

        var (frameGroup, vertexBuffer) = BuildFrameGroupAndVertices(renderer, program);

        var depthDesc = new TextureDesc(
            Name: "scene-depth",
            Width: 32, Height: 32, DepthOrArrayLayers: 1,
            MipLevelCount: 1, SampleCount: 1,
            Dimension: TextureDimension.D2,
            Format: TextureFormat.Depth32Float,
            Usage: TextureUsage.RenderAttachment);
        var depth = renderer.CreateTexture(in depthDesc);

        return (pipeline, drawGroup, frameGroup, vertexBuffer, depth);
    }

    private static RenderCommandStream EncodeBindingsFrame(
        PipelineHandle pipeline,
        BindGroupHandle drawGroup,
        BindGroupHandle frameGroup,
        BufferHandle vertexBuffer,
        TextureHandle depth,
        uint? dynamicOffset)
    {
        var writer = new ArrayBufferWriter<RenderCommand>(12);
        var encoder = new RenderCommandEncoder(writer);
        encoder.BeginPass(0);
        encoder.SetPipeline(pipeline);
        if (dynamicOffset is { } off) encoder.SetBindGroup(0, drawGroup, off);
        else encoder.SetBindGroup(0, drawGroup);
        encoder.SetBindGroup(1, frameGroup);
        encoder.SetVertexBuffer(0, vertexBuffer, 0, 3 * 20);
        encoder.Draw(new DrawCommand(3, 1, 0, 0));
        encoder.EndPass();

        var passes = new RenderPassDesc[1];
        passes[0] = new RenderPassDesc(colorAttachmentCount: 1)
        {
            // The bindings pipeline declares Depth32Float — the pass must attach it.
            Depth = new DepthAttachmentDesc(depth, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
        };
        passes[0].Colors.Slot0 = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.CornflowerBlue);

        return new RenderCommandStream(writer.WrittenMemory, passes);
    }
}

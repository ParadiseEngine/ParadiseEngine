using System.Buffers;
using TUnit.Core;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>The compute path end to end on a live adapter: the compute.slang fixture writes a
/// gradient into a storage texture inside a <see cref="WebGpuRenderer.SubmitOffscreen"/> stream,
/// the computeBlit.slang raster pass samples it into the headless backbuffer, and ReadbackColor
/// proves pixels moved — the assertion that separates working compute from a pipeline that
/// compiles and dispatches nothing. The throwing paths are pinned alongside because their whole
/// value is failing EAGERLY, in C#, instead of as an async Dawn error that drops work silently.</summary>
public class ComputeGpuTests
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

    private static ShaderProgramDesc LoadCompute() =>
        ShaderProgramLoader.Load(typeof(ComputeGpuTests).Assembly, "Shaders.compute");

    private static ShaderProgramDesc LoadBlit() =>
        ShaderProgramLoader.Load(typeof(ComputeGpuTests).Assembly, "Shaders.computeBlit");

    private sealed record ComputeSetup(
        ComputePipelineHandle Pipeline,
        BindGroupHandle Group,
        TextureViewHandle StorageView,
        TextureHandle StorageTexture);

    private static ComputeSetup CreateComputeSetup(WebGpuRenderer renderer, float boost = 1f)
    {
        var program = LoadCompute();
        var pipeline = renderer.CreateComputePipeline(program);

        var storage = renderer.CreateTexture(new TextureDesc(
            "ComputeTarget", 32, 32, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba16Float, TextureUsage.StorageBinding | TextureUsage.TextureBinding));
        var storageView = renderer.CreateTextureView(new TextureViewDesc(
            "ComputeTargetView", storage, TextureViewDimension.D2, 0, 1));

        ReadOnlySpan<float> tint = [1f, 1f, 1f, 0f];
        var ubo = renderer.CreateBufferWithData(
            new BufferDesc("FillParams", 0, BufferUsage.Uniform), tint);
        ReadOnlySpan<float> read = [boost];
        var readBuffer = renderer.CreateBufferWithData(
            new BufferDesc("ReadValues", 0, BufferUsage.Storage), read);
        var writeBuffer = renderer.CreateBuffer(
            new BufferDesc("WriteValues", 4, BufferUsage.Storage));

        var layout = FindGroup(program, 0);
        var group = renderer.CreateBindGroup(new BindGroupDesc("ComputeGroup", layout, new[]
        {
            BindGroupEntryDesc.ForBuffer(0, ubo, 0, 16),
            BindGroupEntryDesc.ForBuffer(1, readBuffer, 0, 4),
            BindGroupEntryDesc.ForBuffer(2, writeBuffer, 0, 4),
            BindGroupEntryDesc.ForTextureView(3, storageView),
        }));

        return new ComputeSetup(pipeline, group, storageView, storage);
    }

    private static BindGroupLayoutDesc FindGroup(ShaderProgramDesc program, uint index)
    {
        foreach (var group in program.Layout.Groups)
        {
            if (group.GroupIndex == index) return group;
        }
        throw new InvalidOperationException($"Program reflects no group {index}.");
    }

    private static RenderCommandStream ComputeStream(ArrayBufferWriter<RenderCommand> writer, in ComputeSetup setup)
    {
        var encoder = new RenderCommandEncoder(writer);
        encoder.BeginComputePass();
        encoder.SetComputePipeline(setup.Pipeline);
        encoder.SetBindGroup(0, setup.Group);
        encoder.Dispatch(new DispatchCommand(4, 4, 1)); // 32/8 workgroups per axis
        encoder.EndComputePass();
        return new RenderCommandStream(writer.WrittenMemory, ReadOnlyMemory<RenderPassDesc>.Empty);
    }

    private static byte[] BlitAndReadback(WebGpuRenderer renderer, TextureViewHandle sourceView)
    {
        var blit = LoadBlit();
        var pipeline = renderer.CreatePipeline(blit, renderer.ColorFormat);
        var sampler = renderer.CreateSampler(new SamplerDesc(
            "BlitSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest, 1));
        var group = renderer.CreateBindGroup(new BindGroupDesc("BlitGroup", FindGroup(blit, 0), new[]
        {
            BindGroupEntryDesc.ForTextureView(0, sourceView),
            BindGroupEntryDesc.ForSampler(1, sampler),
        }));

        var writer = new ArrayBufferWriter<RenderCommand>(16);
        var encoder = new RenderCommandEncoder(writer);
        encoder.BeginPass(0);
        encoder.SetPipeline(pipeline);
        encoder.SetBindGroup(0, group);
        encoder.Draw(new DrawCommand(3, 1, 0, 0));
        encoder.EndPass();

        var pass = new RenderPassDesc();
        pass.ColorAttachmentCount = 1;
        pass[0] = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
        renderer.Submit(new RenderCommandStream(writer.WrittenMemory, new[] { pass }));
        return renderer.ReadbackColor(out _, out _);
    }

    [Test]
    public async Task compute_dispatch_in_an_offscreen_submit_writes_pixels_a_render_pass_samples()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var setup = CreateComputeSetup(renderer);

            // Control: blit the never-dispatched (zero-initialized) storage texture → black.
            var before = BlitAndReadback(renderer, setup.StorageView);

            var writer = new ArrayBufferWriter<RenderCommand>(16);
            renderer.SubmitOffscreen(ComputeStream(writer, setup));
            var after = BlitAndReadback(renderer, setup.StorageView);

            await Assert.That(before.AsSpan().SequenceEqual(after)).IsFalse();
            // The kernel writes a u/v gradient with blue = 1-u: the top-left corner must be
            // strongly blue and the top-right strongly red (BGRA readback order).
            var w = 32;
            var topLeftBlue = after[0];
            var topRightRed = after[(w - 1) * 4 + 2];
            await Assert.That((int)topLeftBlue).IsGreaterThan(200);
            await Assert.That((int)topRightRed).IsGreaterThan(200);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task offscreen_submit_rejects_backbuffer_passes()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var writer = new ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.EndPass();
            var pass = new RenderPassDesc();
            pass.ColorAttachmentCount = 1;
            pass[0] = new ColorAttachmentDesc(RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, ColorRgba.Black);
            var stream = new RenderCommandStream(writer.WrittenMemory, new[] { pass });

            await Assert.That(() => renderer.SubmitOffscreen(in stream)).Throws<InvalidOperationException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task render_commands_inside_a_compute_pass_throw_eagerly()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var setup = CreateComputeSetup(renderer);
            var writer = new ArrayBufferWriter<RenderCommand>(8);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginComputePass();
            encoder.SetComputePipeline(setup.Pipeline);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndComputePass();
            var stream = new RenderCommandStream(writer.WrittenMemory, ReadOnlyMemory<RenderPassDesc>.Empty);

            await Assert.That(() => renderer.SubmitOffscreen(in stream)).Throws<InvalidOperationException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task an_unclosed_compute_pass_throws()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var writer = new ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginComputePass();
            var stream = new RenderCommandStream(writer.WrittenMemory, ReadOnlyMemory<RenderPassDesc>.Empty);

            await Assert.That(() => renderer.SubmitOffscreen(in stream)).Throws<InvalidOperationException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroyed_compute_pipeline_handles_are_stale()
    {
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;
        try
        {
            var setup = CreateComputeSetup(renderer);
            renderer.DestroyComputePipeline(setup.Pipeline);

            var writer = new ArrayBufferWriter<RenderCommand>(8);
            var stream = ComputeStream(writer, setup);
            await Assert.That(() => renderer.SubmitOffscreen(in stream)).Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task loader_reflects_storage_entries_with_compute_visibility()
    {
        // Pure reflection — no adapter needed. Pins the D6/D7 loader rules against the fixture.
        var program = LoadCompute();
        var group = FindGroup(program, 0);

        await Assert.That(group.Entries.Length).IsEqualTo(4);
        // Compute-only file → every entry Compute-visible.
        foreach (var entry in group.Entries)
        {
            await Assert.That(entry.Visibility).IsEqualTo(ShaderStage.Compute);
        }
        await Assert.That(group.Entries[0].Type).IsEqualTo(BindingResourceType.UniformBuffer);
        await Assert.That(group.Entries[1].Type).IsEqualTo(BindingResourceType.ReadonlyStorageBuffer);
        // RWStructuredBuffer → writable storage.
        await Assert.That(group.Entries[2].Type).IsEqualTo(BindingResourceType.StorageBuffer);
        // WTexture2D + [format("rgba16f")] → write-only storage texture with the parsed format.
        await Assert.That(group.Entries[3].Type).IsEqualTo(BindingResourceType.StorageTexture);
        await Assert.That(group.Entries[3].StorageFormat).IsEqualTo(TextureFormat.Rgba16Float);
        await Assert.That(group.Entries[3].Access).IsEqualTo(StorageTextureAccess.WriteOnly);
    }
}

using System;
using System.Reflection;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Regression tests for the iteration-3 OpenCara findings around handle identity:
///
/// (1) <c>BeginPass</c> must reject non-null <c>RenderPassDesc.Depth</c> with
///     <see cref="NotSupportedException"/> — symmetric with the
///     <c>CreatePipeline(DepthStencilFormat)</c> guard. The iteration-2 commit message claimed
///     this was added; iteration-2.5 verdict caught the omission.
///
/// (2) <c>DestroyShader</c> must evict the dedupe cache SYNCHRONOUSLY at schedule time so a
///     <c>CreateShader</c> call between schedule and the deferred slot-table remove compiles a
///     fresh module instead of returning the dying handle (use-after-free guard).
///
/// (3) <c>CreatePipeline</c> must mint a fresh <see cref="PipelineHandle"/> per call even when
///     the underlying native pipeline is shared via cache — destroying one handle must not
///     invalidate the other (matches the contract of every other resource type).</summary>
public class HandleDistinctnessTests
{
    private static WebGpuRenderer? TryCreateHeadlessOrSkip(uint width = 16, uint height = 16)
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

    private static ShaderProgramDesc LoadTriangleProgram() =>
        WebGpuRenderer.LoadShaderProgram(typeof(HandleDistinctnessTests).Assembly, "Shaders.triangle");

    [Test]
    public async Task begin_pass_rejects_non_null_depth_attachment()
    {
        // (1) Symmetric guard — the M1 backend doesn't plumb depth-stencil into the pass
        // descriptor; a caller setting pass.Depth must see NotSupportedException at submit time
        // instead of a silently depth-less render pass. Companion to the existing
        // CreatePipeline(DepthStencilFormat) guard test.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat);

            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1)
            {
                Depth = new DepthAttachmentDesc(
                    DepthTexture: TextureHandle.Invalid,
                    DepthLoad: LoadOp.Clear,
                    DepthStore: StoreOp.Store,
                    ClearDepth: 1f),
            };
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid,
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: ColorRgba.Black);

            var writer = new System.Buffers.ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(pipeline);
            encoder.EndPass();
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);

            await Assert.That(() => renderer.Submit(in stream)).Throws<NotSupportedException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroy_shader_then_recreate_with_same_content_returns_distinct_handle()
    {
        // (2) Use-after-free guard — DestroyShader must evict the (Wgsl, EntryPoint, Stage) cache
        // entry SYNCHRONOUSLY at schedule time. If eviction were deferred (as it was pre-fix), a
        // re-create within the deferred-destruction window would return the dying handle and the
        // scheduled DestroyShader callback would invalidate it under the new caller.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var vsModule = program.Modules[0];

            var h1 = renderer.CreateShader(in vsModule);
            renderer.DestroyShader(h1);
            var h2 = renderer.CreateShader(in vsModule);

            // h2 must NOT be h1 — same slot index is fine (pool reuse), but the generation must
            // be strictly newer so the old handle stops resolving.
            await Assert.That(h2.Equals(h1)).IsFalse();
            await Assert.That(h2.IsValid).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroy_buffer_invalidates_handle_synchronously()
    {
        // Iteration-4 stale-handle contract: DestroyBuffer must make the handle un-resolvable the
        // instant it returns, not N frames later. A RenderCommandStream that uses the destroyed
        // handle must fail with StaleHandleException on Submit — not silently succeed because the
        // deferred destroy hasn't fired yet.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var desc = new BufferDesc("stale-probe", 64, BufferUsage.Vertex);
            var h = renderer.CreateBuffer(in desc);
            renderer.DestroyBuffer(h);

            // Build a minimal command stream that references h after destroy.
            var program = LoadTriangleProgram();
            var pipeline = renderer.CreatePipeline(program, renderer.ColorFormat);
            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid,
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: ColorRgba.Black);
            var writer = new System.Buffers.ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(pipeline);
            encoder.SetVertexBuffer(0, h, 0, 64);
            encoder.EndPass();
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);

            await Assert.That(() => renderer.Submit(in stream)).Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroy_pipeline_invalidates_handle_synchronously()
    {
        // Companion of the buffer test: DestroyPipeline invalidates the public handle at once.
        // The native RenderPipeline is cache-owned so a second live handle to the same native
        // stays resolvable (already covered by
        // two_create_pipeline_calls_return_distinct_handles_with_shared_native_cache).
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var p = renderer.CreatePipeline(program, renderer.ColorFormat);
            renderer.DestroyPipeline(p);

            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid,
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: ColorRgba.Black);
            var writer = new System.Buffers.ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(p);
            encoder.EndPass();
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);

            await Assert.That(() => renderer.Submit(in stream)).Throws<StaleHandleException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task two_create_pipeline_calls_return_distinct_handles_with_shared_native_cache()
    {
        // (3) Pipeline cache below public handle layer — two CreatePipeline calls with structurally-
        // equal descs share the underlying native pipeline (cache hit) but receive DISTINCT
        // PipelineHandle values. This is the contract that lets one consumer destroy its handle
        // without invalidating another consumer's handle to the same native resource.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var p1 = renderer.CreatePipeline(program, renderer.ColorFormat);
            var p2 = renderer.CreatePipeline(program, renderer.ColorFormat);

            await Assert.That(p1.Equals(p2)).IsFalse();
            await Assert.That(p1.IsValid).IsTrue();
            await Assert.That(p2.IsValid).IsTrue();

            // Destroying p1 must NOT invalidate p2 — the cache holds the native pipeline below
            // the handle layer, so p2's slot still resolves cleanly.
            renderer.DestroyPipeline(p1);
            // p2 still resolvable — exercise it via a no-op render pass to confirm the native
            // pipeline is still alive.
            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1);
            passes[0].Colors.Slot0 = new ColorAttachmentDesc(
                View: RenderViewHandle.Invalid,
                Load: LoadOp.Clear,
                Store: StoreOp.Store,
                ClearValue: ColorRgba.CornflowerBlue);

            var writer = new System.Buffers.ArrayBufferWriter<RenderCommand>(4);
            var encoder = new RenderCommandEncoder(writer);
            encoder.BeginPass(0);
            encoder.SetPipeline(p2);
            encoder.EndPass();
            var stream = new RenderCommandStream(writer.WrittenMemory, passes);
            // Submit must not throw — p2's slot still points at the cached native pipeline.
            renderer.Submit(in stream);
        }
        finally
        {
            renderer.Dispose();
        }
    }
}

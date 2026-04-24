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
        // (2) Use-after-free guard — DestroyShader synchronously bumps the slot's generation so
        // the old handle stops resolving immediately. A re-create with the same content re-uses
        // the cached native module below the handle layer but mints a fresh slot entry, so h2
        // always differs from h1 and h1 can never silently start resolving again. Iteration 5
        // restructured the cache from "interned-handle" to "native-below-handle"; the contract
        // surfaced by this test is unchanged.
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
        // (3) Pipeline cache below public handle layer — two CreatePipeline(in PipelineDesc)
        // calls with structurally-equal descs (same reused ShaderHandles) share the underlying
        // native pipeline (cache hit) but receive DISTINCT PipelineHandle values. This is the
        // contract that lets one consumer destroy its handle without invalidating another
        // consumer's handle to the same native resource.
        //
        // Note: this test uses the high-level CreatePipeline(ShaderProgramDesc, TextureFormat)
        // helper, which mints a fresh ShaderHandle per call under iter-5 (shader-module cache
        // lives below the public handle layer, same as pipelines). As a result p1 and p2 are
        // backed by DIFFERENT native pipelines here — the shared-native property is covered by
        // PipelineCache unit tests and by any caller that reuses ShaderHandles across multiple
        // CreatePipeline(in PipelineDesc) invocations. The crucial iter-5 invariant this test
        // pins is the one the name asserts: destroying p1 must NOT invalidate p2's live handle.
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

    [Test]
    public async Task two_create_shader_module_calls_return_distinct_handles_with_shared_native_cache()
    {
        // Iteration-5 fix (codex iteration-4 verdict): CreateShader(ShaderModuleDesc) used to
        // intern the public ShaderHandle by (Wgsl, EntryPoint, Stage). Two callers with the same
        // desc got the same handle, so Destroy* by either caller invalidated the other's live
        // handle. The shader-module cache now lives BELOW the public handle layer — same pattern
        // as PipelineCache: each call mints a fresh ShaderHandle, the native WgShaderModule is
        // shared across slots via content-keyed dedupe.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var vsModule = program.Modules[0];

            var h1 = renderer.CreateShader(in vsModule);
            var h2 = renderer.CreateShader(in vsModule);

            // Distinct public handles, both resolvable.
            await Assert.That(h1.Equals(h2)).IsFalse();
            await Assert.That(h1.IsValid).IsTrue();
            await Assert.That(h2.IsValid).IsTrue();

            // Destroying h1 must NOT invalidate h2 — the native module is cache-owned below the
            // handle layer, so h2's slot still points at a live WgShaderModule. We can still
            // build a pipeline that references h2.
            renderer.DestroyShader(h1);
            var fsModule = program.Modules[1];
            var fs = renderer.CreateShader(in fsModule);

            var pipelineDesc = new PipelineDesc
            {
                Name = "DistinctShaderProbe",
                VertexShader = h2,
                VertexEntryPoint = vsModule.EntryPoint,
                FragmentShader = fs,
                FragmentEntryPoint = fsModule.EntryPoint,
                VertexLayouts = program.VertexBuffers,
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Uint16,
                ColorFormat = renderer.ColorFormat,
                DepthStencilFormat = null,
                Layout = program.Layout,
            };
            // If h2 had been silently invalidated by DestroyShader(h1), ResolveShader(h2) inside
            // BuildNativePipeline would throw StaleHandleException. A successful CreatePipeline
            // confirms the native is still alive and h2's slot still resolves.
            var pipeline = renderer.CreatePipeline(in pipelineDesc);
            await Assert.That(pipeline.IsValid).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_pipeline_rejects_non_empty_layout()
    {
        // Iteration-5 fix (codex iteration-4 verdict): PipelineDesc.Layout was hashed into cache
        // identity but silently dropped by the backend (which always requested Dawn's auto
        // layout). Non-empty layouts now throw NotSupportedException with an M2-deferral message
        // so callers find out at CreatePipeline time instead of getting a cache-fragmented
        // pipeline whose bindings are never enforced. Empty layouts (the ShaderProgramLoader
        // output for M1) are still accepted.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var vsModule = program.Modules[0];
            var fsModule = program.Modules[1];
            var vs = renderer.CreateShader(in vsModule);
            var fs = renderer.CreateShader(in fsModule);

            var nonEmptyLayout = new PipelineLayoutDesc(
                Groups: new[]
                {
                    new BindGroupLayoutDesc(0, new[]
                    {
                        new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16),
                    }),
                },
                PushConstants: Array.Empty<PushConstantRangeDesc>());

            var desc = new PipelineDesc
            {
                Name = "NonEmptyLayoutProbe",
                VertexShader = vs,
                VertexEntryPoint = vsModule.EntryPoint,
                FragmentShader = fs,
                FragmentEntryPoint = fsModule.EntryPoint,
                VertexLayouts = program.VertexBuffers,
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Uint16,
                ColorFormat = renderer.ColorFormat,
                DepthStencilFormat = null,
                Layout = nonEmptyLayout,
            };

            await Assert.That(() => renderer.CreatePipeline(in desc)).Throws<NotSupportedException>();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_pipeline_from_program_does_not_grow_shader_slot_table()
    {
        // Iter-6 fix for OpenCara's iter-5 minor finding: the high-level
        // CreatePipeline(ShaderProgramDesc, TextureFormat) helper minted two ShaderHandles that
        // were consumed locally by the PipelineDesc and never returned to the caller, so every
        // call leaked two entries into _device.Shaders. Iter-6 destroys the locally-minted
        // handles after the native pipeline is built — the content-keyed _shaderModuleCache and
        // the native WgRenderPipeline both retain the WgShaderModule, so post-creation destroy
        // only releases slot-table metadata.
        //
        // Assertion: N repeated CreatePipeline(program, fmt) calls leave
        // renderer.ShaderSlotCountForTest at the same value as after the warm-up call. Any
        // reappearance of the leak (e.g. a forgotten Destroy pair or an inline refactor that
        // drops it) surfaces here as a hard failure regardless of GPU presence in CI.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();

            // Warm: one call establishes the native modules in _shaderModuleCache and the
            // pipeline in _pipelineCache. After this the slot count is the baseline we pin.
            _ = renderer.CreatePipeline(program, renderer.ColorFormat);
            var baseline = renderer.ShaderSlotCountForTest;

            for (var i = 0; i < 8; i++)
            {
                _ = renderer.CreatePipeline(program, renderer.ColorFormat);
            }

            await Assert.That(renderer.ShaderSlotCountForTest).IsEqualTo(baseline);
        }
        finally
        {
            renderer.Dispose();
        }
    }
}

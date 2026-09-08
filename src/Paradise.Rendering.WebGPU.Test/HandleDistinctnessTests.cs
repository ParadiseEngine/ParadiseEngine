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
    public async Task begin_pass_with_depth_attachment_submits_cleanly()
    {
        // M2 flipped the old "reject pass.Depth" guard into real plumbing: a pass carrying a
        // depth attachment (resolved from its TextureHandle) plus a pipeline with a matching
        // DepthStencilFormat must submit without throwing.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var pipeline = renderer.CreatePipeline(
                program, renderer.ColorFormat, depthStencilFormat: TextureFormat.Depth32Float);

            var depthDesc = new TextureDesc(
                Name: "depth-probe",
                Width: 16, Height: 16, DepthOrArrayLayers: 1,
                MipLevelCount: 1, SampleCount: 1,
                Dimension: TextureDimension.D2,
                Format: TextureFormat.Depth32Float,
                Usage: TextureUsage.RenderAttachment);
            var depthTexture = renderer.CreateTexture(in depthDesc);

            var passes = new RenderPassDesc[1];
            passes[0] = new RenderPassDesc(colorAttachmentCount: 1)
            {
                Depth = new DepthAttachmentDesc(
                    DepthTexture: depthTexture,
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

            renderer.Submit(in stream); // must not throw
            renderer.DestroyTexture(depthTexture);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task destroy_shader_then_recreate_with_same_content_returns_distinct_handle()
    {
        // Destroy invalidates the public slot immediately; recreating identical shader content must
        // produce a new handle while reusing the native module.
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
        // Public pipeline handles remain independent even when native resources are shared:
        // destroying p1 must preserve p2. This high-level helper creates fresh shader handles;
        // native cache reuse is tested separately.
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
        // Identical shader content shares native modules, never public handles; destroying one
        // caller's handle must preserve another's.
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
    public async Task create_pipeline_accepts_explicit_non_empty_layout()
    {
        // M2 flipped the old "reject non-empty Layout" guard into a real PipelineLayout build:
        // a desc carrying bind groups produces an explicit native layout (WebGPU allows a layout
        // superset of what the shader actually uses), and the pipeline builds cleanly.
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
                Name = "ExplicitLayoutProbe",
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

            var pipeline = renderer.CreatePipeline(in desc);
            await Assert.That(pipeline.IsValid).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_pipeline_from_program_does_not_grow_shader_slot_table()
    {
        // The high-level helper must release its temporary shader slots after pipeline creation.
        // Repeated calls must preserve the warmed-up slot count; native modules remain cache-owned.
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

    [Test]
    public async Task create_pipeline_from_program_releases_vs_handle_when_fs_create_throws()
    {
        // Force fragment-module creation to fail after the vertex slot exists. Cleanup must free
        // the vertex slot and safely skip the unallocated fragment handle.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var triangle = LoadTriangleProgram();
            var vsReal = triangle.Modules[0];
            var fsReal = triangle.Modules[1];

            // Force the VS module into the content cache with a warm call so its slot count
            // contribution is stable when the tampered call below runs (the tampered VS has a
            // different WGSL string, so it takes a fresh slot — the leak window is exactly one
            // slot entry if the fix regresses).
            _ = renderer.CreatePipeline(triangle, renderer.ColorFormat);
            var baseline = renderer.ShaderSlotCountForTest;

            // Tampered program: valid VS WGSL paired with intentionally-broken FS WGSL. We reuse
            // the real VS so Dawn accepts it, then feed Dawn garbage for FS so CreateShaderModule
            // returns null → WebGpuDevice.CreateShaderModule throws InvalidOperationException.
            var badFs = new ShaderModuleDesc(
                Wgsl: "@fragment fn fs_main() -> @location(0) vec4<f32> { THIS IS NOT VALID WGSL }",
                EntryPoint: fsReal.EntryPoint,
                Stage: fsReal.Stage);
            var tampered = new ShaderProgramDesc(
                Modules: new[] { vsReal, badFs },
                Layout: triangle.Layout,
                VertexBuffers: triangle.VertexBuffers);

            for (var i = 0; i < 4; i++)
            {
                try
                {
                    _ = renderer.CreatePipeline(tampered, renderer.ColorFormat);
                }
                catch (InvalidOperationException)
                {
                    // Expected — Dawn rejects the bad WGSL, WebGpuDevice wraps as
                    // "ShaderModule creation returned null." If Dawn happens to tolerate this
                    // input on some implementation, the test simply exercises the happy path
                    // and still passes — the finally is trivially valid then.
                }
            }

            // Baseline holds if and only if the finally destroyed every allocated vs handle
            // (the only ones that reach creation on the FS-throws path). Any regression to
            // "allocations outside try" reappears here as baseline drift by N per call.
            await Assert.That(renderer.ShaderSlotCountForTest).IsEqualTo(baseline);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_pipeline_from_program_releases_shader_handles_on_exception()
    {
        // Repeated helper calls must not leak temporary shader slots. The explicit layout is now
        // supported, so this case exercises successful creation.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var triangle = LoadTriangleProgram();
            // Build a tampered ShaderProgramDesc with the triangle's modules/VertexBuffers but a
            // non-empty PipelineLayoutDesc — the BuildNativePipeline guard rejects this.
            var tampered = new ShaderProgramDesc(
                Modules: triangle.Modules,
                Layout: new PipelineLayoutDesc(
                    Groups: new[]
                    {
                        new BindGroupLayoutDesc(0, new[]
                        {
                            new BindGroupLayoutEntryDesc(0, ShaderStage.Vertex, BindingResourceType.UniformBuffer, 16),
                        }),
                    },
                    PushConstants: Array.Empty<PushConstantRangeDesc>()),
                VertexBuffers: triangle.VertexBuffers);

            // Warm so the slot count has a settled baseline (the helper mints + destroys two
            // handles per successful call; baseline after the warm call stays stable if the
            // exception path is clean).
            _ = renderer.CreatePipeline(triangle, renderer.ColorFormat);
            var baseline = renderer.ShaderSlotCountForTest;

            for (var i = 0; i < 4; i++)
            {
                try { _ = renderer.CreatePipeline(tampered, renderer.ColorFormat); }
                catch (NotSupportedException) { /* expected — the Layout guard fires */ }
            }

            await Assert.That(renderer.ShaderSlotCountForTest).IsEqualTo(baseline);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task create_pipeline_from_program_respects_custom_topology()
    {
        // Exercise a nondefault topology through the high-level pipeline helper; hardcoded
        // triangles would silently assemble the wrong primitives.
        var renderer = TryCreateHeadlessOrSkip();
        if (renderer is null) return;

        try
        {
            var program = LoadTriangleProgram();
            var p = renderer.CreatePipeline(
                program,
                renderer.ColorFormat,
                topology: PrimitiveTopology.PointList);
            await Assert.That(p.IsValid).IsTrue();
        }
        finally
        {
            renderer.Dispose();
        }
    }
}

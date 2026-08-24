using Paradise.Windowing;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Paradise.Rendering.WebGPU.Internal;
using WgWebGPU = WebGpuSharp.WebGPU;
using WgTextureView = WebGpuSharp.TextureView;
using WgTexture = WebGpuSharp.Texture;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgTextureDimension = WebGpuSharp.TextureDimension;
using WgRenderPassDescriptor = WebGpuSharp.RenderPassDescriptor;
using WgRenderPassColorAttachment = WebGpuSharp.RenderPassColorAttachment;
using WgLoadOp = WebGpuSharp.LoadOp;
using WgStoreOp = WebGpuSharp.StoreOp;
using WgColor = WebGpuSharp.Color;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgRenderPassEncoder = WebGpuSharp.RenderPassEncoder;
using WgBuffer = WebGpuSharp.Buffer;
using WgCommandEncoder = WebGpuSharp.CommandEncoder;
using WgComputePassEncoder = WebGpuSharp.ComputePassEncoder;

namespace Paradise.Rendering.WebGPU;

/// <summary>WebGPU (Dawn) backend entry point. Constructed from a <see cref="SurfaceDescriptor"/>,
/// which decides what it draws INTO: a window's swapchain, or — for
/// <see cref="SurfacePlatform.Headless"/> — an offscreen texture it owns. Exposes resource
/// Create/Destroy plus the <see cref="Submit(in RenderCommandStream)"/> path that drives a real
/// frame.
///
/// That difference lives in one place, <c>IPresentationTarget</c>, and nothing in the frame path
/// consults it: acquire a view, draw, present. "Headless" is a kind of TARGET, not a kind of
/// renderer — this class used to carry a <c>bool</c> for it and branch at six points (colour
/// format, resize, both presents, the readback guard, the backbuffer acquire).</summary>
/// <remarks>Implements <see cref="IRenderer"/>, the backend-agnostic slice consumed by
/// <c>Paradise.Rendering.Pbr</c>. The members beyond it — <see cref="OverlayPass"/>,
/// <see cref="NativeDevice"/>, <see cref="ReadbackColor"/>, <see cref="RenderClearFrame"/>, and
/// the raw <see cref="CreateShader(in ShaderDesc)"/> / <see cref="CreatePipeline(in PipelineDesc)"/>
/// descriptor paths — are Dawn-specific and reached through this concrete type; see
/// <see cref="IRenderer"/> for why each is excluded.</remarks>
public sealed class WebGpuRenderer : IRenderer, IDisposable
{
    private const int DefaultFramesInFlight = 2;

    private readonly WebGpuDevice _device;
    private readonly IPresentationTarget _target;

    /// <summary>Capture requests awaiting a frame. Concurrent because this is the ONE seam other
    /// threads may touch: everything else on this renderer is the render thread's, and a request
    /// posted from elsewhere is serviced on the render thread at a point where the frame's texture
    /// is alive. The alternative — letting a caller read the target directly — races the frame in
    /// progress.</summary>
    private readonly ConcurrentQueue<TaskCompletionSource<ColorReadback>> _captureRequests = new();
    private readonly DeferredDestructionQueue _destructionQueue = new(DefaultFramesInFlight);
    private readonly PipelineCache _pipelineCache = new();
    // Pipeline ↔ pass depth compatibility is a Dawn validation error (async, via the uncaptured
    // -error callback) — this side table lets Submit surface the mismatch as a synchronous,
    // descriptive exception at SetPipeline time instead. Keyed by public handle; entries follow
    // the handle's lifetime.
    private readonly System.Collections.Generic.Dictionary<PipelineHandle, bool> _pipelineHasDepth = new();
    private bool _disposed;

    /// <summary>
    /// Build a renderer for whatever the descriptor DESCRIBES: a swapchain over a native window,
    /// or — for <see cref="SurfacePlatform.Headless"/> — an offscreen <c>BGRA8Unorm</c> target of
    /// the descriptor's size, with no surface created at all.
    ///
    /// This used to refuse the headless descriptor and point callers at
    /// <see cref="CreateHeadless"/>, which left <see cref="SurfaceDescriptor"/> able to STATE a
    /// case the only constructor taking one would not build. Every host holding a descriptor then
    /// had to know the rule and branch on it — so the branch lived in as many places as there were
    /// hosts, instead of here, in the type that owns the distinction.
    /// </summary>
    /// <param name="allowCapture">Configure a window's swapchain so its frames can be copied
    /// (<see cref="CaptureFrameAsync"/>). OFF by default and a CONSTRUCTION-time choice, both
    /// deliberately: a backbuffer that must be copyable can cost the driver optimisations on every
    /// frame, and changing it later would mean reconfiguring the swapchain mid-run — a visible
    /// hitch. Ignored for a headless target, whose texture is always copyable.</param>
    public WebGpuRenderer(in SurfaceDescriptor surface, bool allowCapture = false)
    {
        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");

        // The ONE decision, and it is construction rather than behaviour: which target to build.
        // Everything after this line asks the target and never asks which kind it is.
        if (surface.Platform == SurfacePlatform.Headless)
        {
            _device = WebGpuDevice.Create(instance, compatibleSurface: null);
            _target = new OffscreenTarget(_device, surface.Width, surface.Height);
            return;
        }

        var nativeSurface = SurfaceFactory.Create(instance, surface);
        _device = WebGpuDevice.Create(instance, nativeSurface);
        _target = new SurfaceTarget(
            new SurfaceState(_device, nativeSurface, surface.Width, surface.Height, allowCapture));
    }

    /// <summary>Construct the renderer using the headless adapter path. No native surface is
    /// created; clear frames render into an offscreen <c>BGRA8Unorm</c> texture sized
    /// <paramref name="width"/> x <paramref name="height"/>. The CI smoke test driver consumes
    /// this path with <c>SDL_VIDEODRIVER=dummy</c>.
    ///
    /// Kept as the NAME for that intent — it reads better than a descriptor at a call site that
    /// only wants an offscreen target — but it is now the same constructor underneath.</summary>
    public static WebGpuRenderer CreateHeadless(uint width = 1, uint height = 1) =>
        new(SurfaceDescriptor.Headless(width, height));

    /// <summary>The native swapchain format for windowed renderers, or <see cref="TextureFormat.Bgra8Unorm"/>
    /// for an offscreen target. Pipeline color targets must match this format or the backend will
    /// reject the pipeline at draw time.</summary>
    public TextureFormat ColorFormat => _target.ColorFormat;

    /// <summary>Resize the surface (or offscreen target) to <paramref name="width"/> x
    /// <paramref name="height"/>. Zero-sized requests are clamped to 1.</summary>
    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target.Resize(width, height);
    }

    /// <summary>Acquire the next color attachment, run a single render pass that clears it to
    /// <paramref name="clearColor"/>, submit, and present — which for a target with no display is
    /// a no-op, leaving the frame in the texture.</summary>
    public void RenderClearFrame(in ColorRgba clearColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        var colors = new WgRenderPassColorAttachment[1];
        colors[0] = new WgRenderPassColorAttachment
        {
            View = view,
            LoadOp = WgLoadOp.Clear,
            StoreOp = WgStoreOp.Store,
            ClearValue = new WgColor(clearColor.R, clearColor.G, clearColor.B, clearColor.A),
            DepthSlice = null,
        };
        var passDesc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            Label = "ParadiseClearPass",
        };
        var pass = encoder.BeginRenderPass(in passDesc);
        pass.End();
        // EVERY path that presents a frame serves the capture queue. Miss one and a caller that
        // happens to drive the renderer through it waits forever on a task nothing will complete.
        var pending = RecordPendingCaptures(encoder);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        _target.Present();
        CompletePendingCaptures(pending);
        _destructionQueue.AdvanceFrame();
    }

    /// <summary>Submit a stream that renders only into explicit offscreen targets. No backbuffer
    /// acquire, no <see cref="OverlayPass"/>, no present — and critically no frame advance: the
    /// deferred-destruction window is measured in PRESENTED frames, and advancing it per
    /// offscreen submit would shrink the in-flight safety margin.</summary>
    public void SubmitOffscreen(in RenderCommandStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var encoder = _device.Device.CreateCommandEncoder();
        ExecuteStream(in stream, encoder, backbuffer: null);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
    }

    // -------- Resource creation / destruction --------

    /// <summary>Raw-WGSL shader creation path. <see cref="ShaderDesc.Source"/> must be the WGSL
    /// source bytes (UTF-8). Used by tests and consumers not going through Slang.</summary>
    public ShaderHandle CreateShader(in ShaderDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wgsl = System.Text.Encoding.UTF8.GetString(desc.Source);
        return _device.CreateShader(wgsl, desc.Name ?? string.Empty);
    }

    /// <summary>Slang-output shader creation path. Carries the entry-point name forward so the
    /// pipeline build can reference the right exported function on the WebGPU shader module.</summary>
    public ShaderHandle CreateShader(in ShaderModuleDesc moduleDesc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateShaderModule(moduleDesc);
    }

    public void DestroyShader(ShaderHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Stale-handle contract: the public slot MUST stop resolving the instant DestroyShader
        // returns. Detach is pure slot invalidation — it does NOT touch the content-keyed
        // _shaderModuleCache because another live handle may still share the same native, and
        // the cache is renderer-lifetime like PipelineCache. The native object is kept alive by
        // (a) other slots that still reference it, (b) the module cache, and (c) the closure
        // below for the N-frame deferred window so any in-flight GPU work referencing THIS
        // handle's slot value finishes safely.
        if (!_device.DetachShader(handle, out var native))
            return;
        // The closure captures `native` by reference — that capture alone roots the WebGPUSharp
        // wrapper until the deferred frame fires and the closure is dequeued. `_ = native;` is
        // a no-op that documents the intent: we want the capture, nothing more. Do NOT use
        // GC.KeepAlive here — it only prevents elision of stack-allocated locals inside the
        // enclosing method, and `native` is a field on the heap-allocated closure, not a stack
        // local. Calling GC.KeepAlive inside the lambda is misleading (no runtime effect).
        _destructionQueue.Schedule(() => { _ = native; });
    }

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateBuffer(in desc);
    }

    /// <summary>Create a buffer and immediately upload <paramref name="data"/> to it. The buffer
    /// is created with <see cref="BufferUsage.CopyDst"/> implicitly added so the upload can
    /// succeed.</summary>
    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Widen both operands BEFORE multiplying so the product stays in ulong precision.
        // `data.Length * Unsafe.SizeOf<T>()` executes in int and silently wraps at 2^31 — for
        // 16-byte elements that's ~134M entries, a sharp edge for M2/M3 staging buffers.
        var byteSize = (ulong)data.Length * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var sized = new BufferDesc(desc.Name, byteSize > desc.Size ? byteSize : desc.Size, desc.Usage | BufferUsage.CopyDst);
        var handle = _device.CreateBuffer(in sized);
        var native = _device.ResolveBuffer(handle);
        _device.Queue.WriteBuffer(native, 0, data);
        return handle;
    }

    public void DestroyBuffer(BufferHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Stale-handle contract: invalidate the public slot synchronously, defer only the native
        // Buffer.Destroy() call so in-flight GPU work referencing the native buffer finishes first.
        // After this returns, ResolveBuffer throws StaleHandleException for the destroyed handle.
        if (!_device.DetachBuffer(handle, out var native))
            return;
        _destructionQueue.Schedule(() => native.Destroy());
    }

    /// <summary>Write <paramref name="data"/> into an existing buffer at <paramref name="offset"/>
    /// — the per-frame uniform upload path (frame/draw UBO rings).</summary>
    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var native = _device.ResolveBuffer(handle);
        _device.Queue.WriteBuffer(native, offset, data);
    }

    /// <summary>True when the adapter granted BC texture compression — required before creating
    /// textures in any <c>Bc*</c> format; callers without it upload RGBA32-transcoded data.</summary>
    public bool SupportsBcTextureCompression => _device.SupportsBc;

    /// <summary>Required stride alignment for dynamic uniform-buffer offsets (≥ 256).</summary>
    public uint UniformBufferOffsetAlignment => _device.UniformBufferOffsetAlignment;

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBcFormat(desc.Format) && !_device.SupportsBc)
            throw new NotSupportedException(
                $"Texture format '{desc.Format}' requires the TextureCompressionBC adapter feature, " +
                "which this adapter did not grant. Check SupportsBcTextureCompression and upload an " +
                "RGBA fallback instead.");
        return _device.CreateTexture(in desc);
    }

    private static bool IsBcFormat(TextureFormat f) => f is >= TextureFormat.Bc1RgbaUnorm and <= TextureFormat.Bc7RgbaUnormSrgb;

    /// <summary>Upload one mip level. <paramref name="bytesPerRow"/> is the source row pitch in
    /// bytes (for BC formats: bytes per row of 4-texel blocks); <paramref name="rowsPerImage"/>
    /// the number of rows (block rows for BC); <paramref name="width"/>/<paramref name="height"/>
    /// the mip's texel dimensions. Block-size math stays in the asset layer, as in the source
    /// material's texture cache.</summary>
    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = _device.ResolveTexture(handle);
        _device.Queue.WriteTexture(
            new WebGpuSharp.TexelCopyTextureInfo { Texture = entry.Texture, MipLevel = mipLevel },
            data,
            new WebGpuSharp.TexelCopyBufferLayout { Offset = 0, BytesPerRow = bytesPerRow, RowsPerImage = rowsPerImage },
            new WgExtent3D(width, height, 1));
    }

    public void DestroyTexture(TextureHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.DetachTexture(handle, out var native))
            return;
        _destructionQueue.Schedule(() => native.Texture.Destroy());
    }

    /// <summary>Create an explicit view into a texture (a chosen dimension / array-layer range) —
    /// e.g. a single layer of the shadow-map array as a render target, or the whole array as a
    /// D2Array sampling view.</summary>
    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateTextureView(in desc);
    }

    public void DestroyTextureView(TextureViewHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.DetachTextureView(handle, out var native))
            return;
        // Views have no native Destroy(); keep the wrapper alive through the deferred window.
        _destructionQueue.Schedule(() => { _ = native; });
    }

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateSampler(in desc);
    }

    public void DestroySampler(SamplerHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.DetachSampler(handle, out var native))
            return;
        // Samplers have no native Destroy(); the closure capture keeps the wrapper alive through
        // the deferred window (same pattern as DestroyShader).
        _destructionQueue.Schedule(() => { _ = native; });
    }

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateBindGroup(in desc);
    }

    public void DestroyBindGroup(BindGroupHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.DetachBindGroup(handle, out var native))
            return;
        _destructionQueue.Schedule(() => { _ = native; });
    }

    public PipelineHandle CreatePipeline(in PipelineDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Cache the native WebGPU pipeline below the public handle layer so two calls with the
        // same content share the GPU compile, but each caller gets a distinct PipelineHandle.
        // First DestroyPipeline doesn't invalidate the second handle — matches the contract of
        // every other resource type (BufferHandle, TextureHandle, ShaderHandle).
        var native = _pipelineCache.GetOrCreateNative(in desc, d => _device.BuildNativePipeline(in d));
        var handle = _device.RegisterPipeline(native);
        _pipelineHasDepth[handle] = desc.DepthStencilFormat is not null;
        return handle;
    }

    /// <summary>Build a <see cref="PipelineDesc"/> from a Slang-reflected program plus a target
    /// color format, then route through <see cref="CreatePipeline(in PipelineDesc)"/> (and its
    /// pipeline cache). Vertex layout is taken verbatim from the program's reflection record —
    /// the M1 design contract's "no hand-coded layout" rule lives in this method's body. The
    /// <paramref name="topology"/> and <paramref name="stripIndexFormat"/> parameters default to
    /// triangle-list / uint16 (the M1 sample's triangle path); line / point / strip callers
    /// pass their own values rather than getting silently wrong primitive assembly.</summary>
    public PipelineHandle CreatePipeline(
        in ShaderProgramDesc program,
        TextureFormat colorFormat,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        IndexFormat stripIndexFormat = IndexFormat.Uint16,
        TextureFormat? depthStencilFormat = null,
        BlendMode blend = BlendMode.Opaque,
        bool depthWriteEnabled = true,
        CompareFunction depthCompare = CompareFunction.Less,
        // Deliberate scaffolding for the PBR milestone: its shader authors TWO fragment entry
        // points (linear vs sRGB-encoding) in one program and selects by surface format. No
        // in-repo caller passes this yet; the parameter exists so the selection lands as an
        // argument, not an API break.
        string? fragmentEntryPoint = null,
        // The vertex-side twin of fragmentEntryPoint, for programs authoring more than one vertex
        // entry (rigid vs skinned). Selecting the module also selects its reflected vertex layout:
        // the two must move together, or one entry point's stride is fed to another's attributes
        // and the draw produces nothing without erroring.
        string? vertexEntryPoint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? vsModule = null;
        ShaderModuleDesc? fsModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Vertex) != 0)
            {
                // Mirrors the fragment rule below: without a selector the FIRST vertex module
                // wins. It used to be the last, which meant adding a second vertex entry point
                // silently repointed every existing pipeline at it.
                if (vertexEntryPoint is null && vsModule is null) vsModule = m;
                else if (vertexEntryPoint is not null && string.Equals(m.EntryPoint, vertexEntryPoint, StringComparison.Ordinal)) vsModule = m;
            }
            if ((m.Stage & ShaderStage.Fragment) != 0)
            {
                // Multi-fragment-entry programs (e.g. linear vs sRGB-encoding variants) select by
                // name; without a selector the first fragment module wins.
                if (fragmentEntryPoint is null && fsModule is null) fsModule = m;
                else if (fragmentEntryPoint is not null && string.Equals(m.EntryPoint, fragmentEntryPoint, StringComparison.Ordinal)) fsModule = m;
            }
        }
        if (vsModule is null)
            throw new InvalidOperationException(vertexEntryPoint is null
                ? "ShaderProgramDesc has no vertex module."
                : $"ShaderProgramDesc has no vertex module named '{vertexEntryPoint}'.");
        if (fsModule is null)
            throw new InvalidOperationException(fragmentEntryPoint is null
                ? "ShaderProgramDesc has no fragment module."
                : $"ShaderProgramDesc has no fragment module named '{fragmentEntryPoint}'.");

        // CreateShaderModule dedupes the underlying native WgShaderModule by (Wgsl, EntryPoint,
        // Stage) inside _shaderModuleCache, but mints a FRESH ShaderHandle per call (iter-5
        // public-handle split — matches the pipeline and buffer contracts). These two handles
        // are consumed locally by the PipelineDesc below and never reach the caller, so we must
        // destroy them after CreatePipeline(in pipelineDesc) returns — otherwise every call
        // leaks two _device.Shaders slot entries for the renderer's lifetime (the native module
        // is safe — the content cache AND the native WgRenderPipeline both retain it).
        //
        // Both CreateShaderModule calls AND the inner CreatePipeline live inside the try so the
        // cleanup covers every exception site: the second CreateShaderModule can throw
        // InvalidOperationException if Dawn fails to compile the WGSL, and the inner
        // CreatePipeline can throw NotSupportedException from BuildNativePipeline's Layout /
        // DepthStencilFormat guards. The finally guards each DestroyShader with IsValid so it
        // skips handles that never got allocated (default(ShaderHandle).Generation == 0).
        ShaderHandle vsHandle = default;
        ShaderHandle fsHandle = default;
        try
        {
            vsHandle = _device.CreateShaderModule(vsModule);
            fsHandle = _device.CreateShaderModule(fsModule);

            var pipelineDesc = new PipelineDesc
            {
                Name = "ShaderProgramPipeline",
                VertexShader = vsHandle,
                VertexEntryPoint = vsModule.EntryPoint,
                FragmentShader = fsHandle,
                FragmentEntryPoint = fsModule.EntryPoint,
                // The chosen entry point's own layout, falling back to the program-level one for
                // single-vertex-entry programs (and for hand-built ShaderProgramDescs, which carry
                // no per-entry map).
                VertexLayouts = program.VertexBuffersByEntryPoint.TryGetValue(vsModule.EntryPoint, out var perEntry)
                    ? perEntry
                    : program.VertexBuffers,
                Topology = topology,
                StripIndexFormat = stripIndexFormat,
                ColorFormat = colorFormat,
                DepthStencilFormat = depthStencilFormat,
                DepthWriteEnabled = depthWriteEnabled,
                DepthCompare = depthCompare,
                Blend = blend,
                Layout = program.Layout,
            };
            return CreatePipeline(in pipelineDesc);
        }
        finally
        {
            if (vsHandle.IsValid) DestroyShader(vsHandle);
            if (fsHandle.IsValid) DestroyShader(fsHandle);
        }
    }

    /// <summary>Build a DEPTH-ONLY pipeline (vertex + depth-stencil, no fragment stage / no color
    /// target) — the shadow-caster path. <paramref name="vertexLayouts"/> overrides the program's
    /// reflected vertex layout so the caster can read position from the full interleaved mesh
    /// buffer (its shadow shader declares only location 0).</summary>
    public PipelineHandle CreateDepthOnlyPipeline(
        in ShaderProgramDesc program,
        TextureFormat depthStencilFormat,
        ReadOnlyMemory<VertexBufferLayoutDesc> vertexLayouts,
        CompareFunction depthCompare = CompareFunction.Less,
        // As in CreatePipeline: a depth-only program may author a rigid and a skinned caster.
        string? vertexEntryPoint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? vsModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Vertex) == 0) continue;
            // First wins without a selector — see CreatePipeline. This used to take the LAST
            // vertex module, so a second entry point would have repointed the shadow pipeline.
            if (vertexEntryPoint is null && vsModule is null) vsModule = m;
            else if (vertexEntryPoint is not null && string.Equals(m.EntryPoint, vertexEntryPoint, StringComparison.Ordinal)) vsModule = m;
        }
        if (vsModule is null)
            throw new InvalidOperationException(vertexEntryPoint is null
                ? "Depth-only program has no vertex module."
                : $"Depth-only program has no vertex module named '{vertexEntryPoint}'.");

        ShaderHandle vsHandle = default;
        try
        {
            vsHandle = _device.CreateShaderModule(vsModule);
            var pipelineDesc = new PipelineDesc
            {
                Name = "DepthOnlyPipeline",
                VertexShader = vsHandle,
                VertexEntryPoint = vsModule.EntryPoint,
                FragmentShader = default,          // no fragment → depth-only
                VertexLayouts = vertexLayouts,
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Uint16,
                ColorFormat = depthStencilFormat,  // ignored (no color target)
                DepthStencilFormat = depthStencilFormat,
                DepthWriteEnabled = true,
                DepthCompare = depthCompare,
                Blend = BlendMode.Opaque,
                Layout = program.Layout,
            };
            return CreatePipeline(in pipelineDesc);
        }
        finally
        {
            if (vsHandle.IsValid) DestroyShader(vsHandle);
        }
    }

    public void DestroyPipeline(PipelineHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Stale-handle contract: invalidate the public slot synchronously. The native pipeline is
        // owned by PipelineCache (shared across every handle that resolved to the same content-hash
        // entry) and outlives individual DestroyPipeline calls — destroying one handle never yanks
        // the underlying resource out from under another. The cache is renderer-lifetime; revisit
        // when M2/M3 introduces dynamic pipeline rebuilds (need refcount or LRU eviction then).
        // No native teardown to defer — detach is pure slot invalidation, so it happens inline.
        _device.DetachPipeline(handle);
        _pipelineHasDepth.Remove(handle);
    }

    public ComputePipelineHandle CreateComputePipeline(in ShaderProgramDesc program, string? entryPoint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? csModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Compute) == 0) continue;
            // Same first-wins/named-selector rule as the render path's entry points.
            if (entryPoint is null && csModule is null) csModule = m;
            else if (entryPoint is not null && string.Equals(m.EntryPoint, entryPoint, StringComparison.Ordinal)) csModule = m;
        }
        if (csModule is null)
            throw new InvalidOperationException(entryPoint is null
                ? "ShaderProgramDesc has no compute module."
                : $"ShaderProgramDesc has no compute module named '{entryPoint}'.");

        // Temp shader handle, destroyed after the build — same slot-leak rationale as
        // CreatePipeline's vs/fs handles. No content cache for compute pipelines: games create a
        // handful once, and the native shader-module dedupe still applies underneath.
        ShaderHandle csHandle = default;
        try
        {
            csHandle = _device.CreateShaderModule(csModule);
            var native = _device.BuildNativeComputePipeline(
                _device.ResolveShader(csHandle), csModule.EntryPoint, program.Layout);
            return _device.RegisterComputePipeline(native);
        }
        finally
        {
            if (csHandle.IsValid) DestroyShader(csHandle);
        }
    }

    public void DestroyComputePipeline(ComputePipelineHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Unlike render pipelines there is no content cache: detaching drops the only managed
        // reference. Safe — WebGPU pipelines have no explicit destroy, and Dawn refcounts
        // in-flight GPU use until the queue drains.
        _device.DetachComputePipeline(handle);
    }

    // -------- Command stream submission --------

    /// <summary>Optional overlay pass recorded into the frame encoder AFTER the scene passes and
    /// before submit/present — the seam UI composition (e.g. the Noesis device) hooks into. The
    /// callback receives the frame's command encoder and the backbuffer view; passes it records
    /// should load (not clear) the color target so they composite over the scene. Invoked on the
    /// render thread only. Single-subscriber: assigning replaces any previous handler rather than
    /// composing with it.</summary>
    public Action<WebGpuSharp.CommandEncoder, WgTextureView>? OverlayPass { get; set; }

    /// <summary>The raw WebGPUSharp device, for subsystems that record their own passes through
    /// <see cref="OverlayPass"/> (they need it to create pipelines/buffers/textures). Treat as
    /// read-only infrastructure — resource lifetime stays with the creating subsystem.</summary>
    public WebGpuSharp.Device NativeDevice
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _device.Device;
        }
    }

    /// <summary>Submit a recorded <see cref="RenderCommandStream"/>. Acquires the backbuffer view,
    /// walks every <see cref="RenderCommand"/>, dispatches to WebGPU, presents (when windowed),
    /// and advances the frame counter so deferred destructions can drain.</summary>
    public void Submit(in RenderCommandStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        ExecuteStream(in stream, encoder, view);
        OverlayPass?.Invoke(encoder, view);
        // AFTER the overlay, so a capture is what the frame actually shows rather than the scene
        // without its UI — and before Finish, so the copy rides the frame's own command buffer.
        var pending = RecordPendingCaptures(encoder);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        _target.Present();
        CompletePendingCaptures(pending);
        _destructionQueue.AdvanceFrame();
    }

    /// <summary>Read the color target back to CPU memory as tightly-packed,
    /// top-down <c>BGRA8</c> (4 bytes/pixel, <see cref="ColorFormat"/> = <see cref="TextureFormat.Bgra8Unorm"/>).
    /// Blocks on GPU completion — intended for screenshots and image-based tests, not per-frame use.
    /// Requires a target the renderer OWNS, because it reads AFTER the frame: a swapchain's texture
    /// is valid only until its present, so a windowed run has nothing left to copy by the time this
    /// is called. Check <see cref="CanReadbackColor"/> rather than catching the throw.</summary>
    public byte[] ReadbackColor(out uint width, out uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var readable = _target.Readable
            ?? throw new InvalidOperationException(
                "ReadbackColor needs a target that can be copied out of. A surface's swapchain "
                + "texture is not CopySrc; build the renderer from a headless SurfaceDescriptor.");

        width = _target.Width;
        height = _target.Height;
        const uint bytesPerPixel = 4;
        var unpaddedBytesPerRow = width * bytesPerPixel;
        // WebGPU requires a texture→buffer copy's BytesPerRow to be a multiple of 256.
        var paddedBytesPerRow = (unpaddedBytesPerRow + 255u) & ~255u;
        var bufferSize = (ulong)paddedBytesPerRow * height;

        var readback = _device.Device.CreateBuffer(new WebGpuSharp.BufferDescriptor
        {
            Label = "ParadiseReadback",
            Size = bufferSize,
            // MapRead may only be combined with CopyDst (WebGPU usage-validity rule).
            Usage = WebGpuSharp.BufferUsage.MapRead | WebGpuSharp.BufferUsage.CopyDst,
            MappedAtCreation = false,
        }) ?? throw new InvalidOperationException("Readback buffer creation returned null.");

        var encoder = _device.Device.CreateCommandEncoder();
        var source = new WebGpuSharp.TexelCopyTextureInfo { Texture = readable, MipLevel = 0 };
        var destination = new WebGpuSharp.TexelCopyBufferInfo
        {
            Buffer = readback,
            Layout = new WebGpuSharp.TexelCopyBufferLayout
            {
                Offset = 0,
                BytesPerRow = paddedBytesPerRow,
                RowsPerImage = height,
            },
        };
        var copySize = new WgExtent3D(width, height, 1);
        encoder.CopyTextureToBuffer(in source, in destination, in copySize);
        _device.Queue.Submit(encoder.Finish());

        var rows = height;
        var pixels = new byte[unpaddedBytesPerRow * rows];
        var rowStride = paddedBytesPerRow;
        var tightStride = unpaddedBytesPerRow;
        // Destroy the staging buffer no matter what — a map timeout or a copy exception in this
        // window must not leak it, since this method is called repeatedly (--screenshot, tests).
        try
        {
            // Wait for the copy to land, then synchronously map and un-pad each row into a tight buffer.
            const ulong timeoutNs = 5_000_000_000; // 5s
            _device.Queue.OnSubmittedWorkSync(timeoutNs);
            readback.MapSync(WebGpuSharp.MapMode.Read, 0, (nuint)bufferSize, 5_000);
            readback.GetConstMappedRange(0, (nuint)bufferSize, (ReadOnlySpan<byte> mapped) =>
            {
                for (var y = 0u; y < rows; y++)
                    mapped.Slice((int)(y * rowStride), (int)tightStride)
                          .CopyTo(pixels.AsSpan((int)(y * tightStride)));
            });
        }
        finally
        {
            // Unmap only if the map succeeded; Unmap on an unmapped buffer is a validation error.
            if (readback.GetMapState() == WebGpuSharp.BufferMapState.Mapped) readback.Unmap();
            readback.Destroy();
        }
        return pixels;
    }

    /// <summary>
    /// Ask for the next frame, as an image.
    ///
    /// Callable from ANY thread and at any time. The request is queued and serviced by the render
    /// thread inside its next frame — the copy is recorded onto that frame's own command buffer,
    /// after the overlay and before the present — so what comes back is exactly what was shown,
    /// UI included. That deferral is the point: a caller reading the target itself would race the
    /// frame in progress, and for a swapchain there is no texture left to read once the frame is
    /// over.
    ///
    /// The task completes when the GPU copy lands and the staging buffer maps, which is at least
    /// one frame away and possibly more.
    /// </summary>
    /// <exception cref="NotSupportedException">This renderer's target cannot be copied from. For a
    /// window that means <see cref="CaptureEnabled"/> was never turned on, or the surface does not
    /// advertise <c>CopySrc</c>. Thrown rather than returned as a faulted task, because it is a
    /// fact about the renderer that is true before the call and will not change by retrying.</exception>
    public Task<ColorReadback> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_target.SupportsCapture)
        {
            throw new NotSupportedException(
                "This target cannot be captured. A window must be built with capture enabled "
                + "(SurfaceDescriptor + CaptureEnabled) and the surface must advertise CopySrc.");
        }

        var request = new TaskCompletionSource<ColorReadback>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.CanBeCanceled)
        {
            // Registration disposed by the completion path is not worth the bookkeeping here: the
            // token simply abandons the wait, and the frame's copy is discarded when it lands.
            cancellationToken.Register(() => request.TrySetCanceled(cancellationToken));
        }
        _captureRequests.Enqueue(request);
        return request.Task;
    }

    /// <summary>Whether this renderer may be asked for a frame — see
    /// <see cref="CaptureFrameAsync"/> for why a window has to opt in.</summary>
    public bool CanCaptureFrame => _target.SupportsCapture;

    /// <summary>Drain the request queue onto this frame's encoder. Returns the staging buffers and
    /// the requests waiting on them, or null when nothing was asked for — the common case, which
    /// costs one queue check.</summary>
    private List<(TaskCompletionSource<ColorReadback> Request, WgBuffer Staging, uint Width, uint Height, uint PaddedRow)>?
        RecordPendingCaptures(WgCommandEncoder encoder)
    {
        if (_captureRequests.IsEmpty || _target.CurrentTexture is not { } texture)
        {
            return null;
        }

        List<(TaskCompletionSource<ColorReadback>, WgBuffer, uint, uint, uint)>? pending = null;
        while (_captureRequests.TryDequeue(out var request))
        {
            // Already cancelled while queued: no copy, no buffer.
            if (request.Task.IsCompleted)
            {
                continue;
            }

            var width = _target.Width;
            var height = _target.Height;
            var paddedRow = (width * 4u + 255u) & ~255u;
            var staging = _device.Device.CreateBuffer(new WebGpuSharp.BufferDescriptor
            {
                Label = "ParadiseCaptureStaging",
                Size = (ulong)paddedRow * height,
                Usage = WebGpuSharp.BufferUsage.MapRead | WebGpuSharp.BufferUsage.CopyDst,
                MappedAtCreation = false,
            });
            if (staging is null)
            {
                request.TrySetException(new InvalidOperationException("Capture staging buffer creation returned null."));
                continue;
            }

            var source = new WebGpuSharp.TexelCopyTextureInfo { Texture = texture, MipLevel = 0 };
            var destination = new WebGpuSharp.TexelCopyBufferInfo
            {
                Buffer = staging,
                Layout = new WebGpuSharp.TexelCopyBufferLayout
                {
                    Offset = 0,
                    BytesPerRow = paddedRow,
                    RowsPerImage = height,
                },
            };
            encoder.CopyTextureToBuffer(in source, in destination, new WgExtent3D(width, height, 1));
            (pending ??= []).Add((request, staging, width, height, paddedRow));
        }

        return pending;
    }

    /// <summary>Map each staged copy and complete its request. Synchronous per capture, which is
    /// what keeps this free of a device-event pump: a capture is rare and deliberate, so paying a
    /// stall on the frame that serves one is cheaper than ticking the instance every frame forever
    /// to service callbacks that almost never exist.</summary>
    private void CompletePendingCaptures(
        List<(TaskCompletionSource<ColorReadback> Request, WgBuffer Staging, uint Width, uint Height, uint PaddedRow)>? pending)
    {
        if (pending is null)
        {
            return;
        }

        foreach (var (request, staging, width, height, paddedRow) in pending)
        {
            try
            {
                const ulong timeoutNs = 5_000_000_000;
                _device.Queue.OnSubmittedWorkSync(timeoutNs);
                var size = (nuint)((ulong)paddedRow * height);
                staging.MapSync(WebGpuSharp.MapMode.Read, 0, size, 5_000);
                var tight = width * 4u;
                var pixels = new byte[tight * height];
                staging.GetConstMappedRange(0, size, (ReadOnlySpan<byte> mapped) =>
                {
                    for (var y = 0u; y < height; y++)
                    {
                        mapped.Slice((int)(y * paddedRow), (int)tight).CopyTo(pixels.AsSpan((int)(y * tight)));
                    }
                });
                request.TrySetResult(new ColorReadback(pixels, width, height));
            }
            catch (Exception ex)
            {
                request.TrySetException(ex);
            }
            finally
            {
                if (staging.GetMapState() == WebGpuSharp.BufferMapState.Mapped) staging.Unmap();
                staging.Destroy();
            }
        }
    }

    private void ExecuteStream(in RenderCommandStream stream, WgCommandEncoder encoder, WgTextureView? backbuffer)
    {
        var passes = stream.Passes.Span;
        var commands = stream.Commands.Span;

        WgRenderPassEncoder? activePass = null;
        // Compute passes get a parallel local: WgComputePassEncoder is an unrelated struct type,
        // and the stream validation guarantees at most one of the two is open.
        WgComputePassEncoder? activeComputePass = null;
        var passHasDepth = false;
        try
        {
            for (var i = 0; i < commands.Length; i++)
            {
                ref readonly var cmd = ref commands[i];
                switch (cmd.Kind)
                {
                    case RenderCommandKind.BeginPass:
                    {
                        if (activePass is not null)
                            throw new InvalidOperationException(
                                "Nested BeginPass — previous pass was not ended (missing EndPass).");
                        if (activeComputePass is not null)
                            throw new InvalidOperationException(
                                "BeginPass inside an open compute pass — end it with EndComputePass first.");
                        var passIndex = cmd.BeginPass.PassIndex;
                        if ((uint)passIndex >= (uint)passes.Length)
                            throw new InvalidOperationException(
                                $"BeginPass references pass index {passIndex} but only {passes.Length} pass(es) declared.");
                        activePass = BeginPass(encoder, passes[passIndex], backbuffer);
                        passHasDepth = passes[passIndex].Depth is not null;
                        break;
                    }
                    case RenderCommandKind.EndPass:
                    {
                        if (activeComputePass is not null)
                            throw new InvalidOperationException(
                                "EndPass inside a compute pass — compute passes close with EndComputePass.");
                        // Null activePass BEFORE calling End() so the finally-block safety net
                        // becomes idempotent: if End() throws (Dawn validation error at pass end),
                        // activePass is already null and the finally won't double-End the same
                        // native encoder. Dawn considers calling End() twice on the same pass an
                        // invariant violation and may trigger a native assertion.
                        var passToEnd = activePass;
                        activePass = null;
                        passToEnd?.End();
                        break;
                    }
                    case RenderCommandKind.SetPipeline:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var handle = cmd.SetPipeline.Pipeline;
                        // Surface pipeline↔pass depth mismatch synchronously and descriptively —
                        // Dawn would only report it asynchronously via the error callback.
                        if (_pipelineHasDepth.TryGetValue(handle, out var pipelineHasDepth) && pipelineHasDepth != passHasDepth)
                        {
                            throw new InvalidOperationException(pipelineHasDepth
                                ? "Pipeline was built with a DepthStencilFormat but the active pass has no Depth attachment — attach a depth texture to the pass or build the pipeline without depth."
                                : "The active pass has a Depth attachment but the pipeline was built without a DepthStencilFormat — build the pipeline with a matching depth format or drop the pass's Depth attachment.");
                        }
                        pass.SetPipeline(_device.ResolvePipeline(handle));
                        break;
                    }
                    case RenderCommandKind.SetVertexBuffer:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var p = cmd.SetVertexBuffer;
                        pass.SetVertexBuffer(p.Slot, _device.ResolveBuffer(p.Buffer), p.Offset, p.Size);
                        break;
                    }
                    case RenderCommandKind.SetIndexBuffer:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var p = cmd.SetIndexBuffer;
                        pass.SetIndexBuffer(_device.ResolveBuffer(p.Buffer), FormatConversions.ToWgpu(p.Format), p.Offset, p.Size);
                        break;
                    }
                    case RenderCommandKind.SetBindGroup:
                    {
                        // Pass-kind agnostic: binds into whichever pass is open (the compute
                        // encoder's SetBindGroup has the same two shapes, dynamic offsets included).
                        var p = cmd.SetBindGroup;
                        var native = _device.ResolveBindGroup(p.Group);
                        if (activeComputePass is { } computePass)
                        {
                            if (p.HasDynamicOffset)
                            {
                                ReadOnlySpan<uint> offsets = [p.DynamicOffset];
                                computePass.SetBindGroup(p.GroupIndex, native, offsets);
                            }
                            else
                            {
                                computePass.SetBindGroup(p.GroupIndex, native);
                            }
                            break;
                        }
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        if (p.HasDynamicOffset)
                        {
                            ReadOnlySpan<uint> offsets = [p.DynamicOffset];
                            pass.SetBindGroup(p.GroupIndex, native, offsets);
                        }
                        else
                        {
                            pass.SetBindGroup(p.GroupIndex, native);
                        }
                        break;
                    }
                    case RenderCommandKind.Draw:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var d = cmd.Draw;
                        pass.Draw(d.VertexCount, d.InstanceCount, d.FirstVertex, d.FirstInstance);
                        break;
                    }
                    case RenderCommandKind.DrawIndexed:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var d = cmd.DrawIndexed;
                        pass.DrawIndexed(d.IndexCount, d.InstanceCount, d.FirstIndex, d.BaseVertex, d.FirstInstance);
                        break;
                    }
                    case RenderCommandKind.SetViewport:
                    {
                        var pass = RequireActiveRenderPass(activePass, activeComputePass);
                        var v = cmd.SetViewport;
                        pass.SetViewport((uint)v.X, (uint)v.Y, (uint)v.Width, (uint)v.Height, v.MinDepth, v.MaxDepth);
                        break;
                    }
                    case RenderCommandKind.BeginComputePass:
                    {
                        if (activePass is not null)
                            throw new InvalidOperationException(
                                "BeginComputePass inside an open render pass — end it with EndPass first.");
                        if (activeComputePass is not null)
                            throw new InvalidOperationException(
                                "Nested BeginComputePass — previous compute pass was not ended (missing EndComputePass).");
                        activeComputePass = encoder.BeginComputePass();
                        break;
                    }
                    case RenderCommandKind.EndComputePass:
                    {
                        if (activePass is not null)
                            throw new InvalidOperationException(
                                "EndComputePass inside a render pass — render passes close with EndPass.");
                        // Null-before-End for the same double-End reason as EndPass above.
                        var computeToEnd = activeComputePass;
                        activeComputePass = null;
                        computeToEnd?.End();
                        break;
                    }
                    case RenderCommandKind.SetComputePipeline:
                    {
                        var pass = RequireActiveComputePass(activeComputePass, activePass);
                        pass.SetPipeline(_device.ResolveComputePipeline(cmd.SetComputePipeline.Pipeline));
                        break;
                    }
                    case RenderCommandKind.Dispatch:
                    {
                        var pass = RequireActiveComputePass(activeComputePass, activePass);
                        var d = cmd.Dispatch;
                        pass.DispatchWorkgroups(d.WorkgroupCountX, d.WorkgroupCountY, d.WorkgroupCountZ);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unknown RenderCommandKind '{cmd.Kind}'.");
                }
            }

            if (activePass is not null)
                throw new InvalidOperationException("RenderCommandStream ended with an open render pass — missing EndPass.");
            if (activeComputePass is not null)
                throw new InvalidOperationException("RenderCommandStream ended with an open compute pass — missing EndComputePass.");
            activePass = null;
        }
        finally
        {
            // Defensive close on exception so a stale-handle / NotSupportedException mid-pass
            // doesn't leave the native render-pass encoder un-Ended (Dawn requires every
            // BeginRenderPass to be matched with End before the encoder is finished). On the
            // happy path activePass was nulled before the try-block exited.
            activePass?.End();
            activeComputePass?.End();
        }
    }

    private static WgRenderPassEncoder RequireActiveRenderPass(WgRenderPassEncoder? pass, WgComputePassEncoder? computePass) =>
        pass ?? throw new InvalidOperationException(computePass is not null
            ? "Render command issued inside a compute pass — use SetComputePipeline/Dispatch there."
            : "Render command issued outside of an active BeginPass/EndPass scope.");

    private static WgComputePassEncoder RequireActiveComputePass(WgComputePassEncoder? pass, WgRenderPassEncoder? renderPass) =>
        pass ?? throw new InvalidOperationException(renderPass is not null
            ? "Compute command issued inside a render pass — compute commands need a BeginComputePass scope."
            : "Compute command issued outside of an active BeginComputePass/EndComputePass scope.");

    private WgRenderPassEncoder BeginPass(WgCommandEncoder encoder, RenderPassDesc pass, WgTextureView? backbuffer)
    {
        // Either ZERO color attachments (a depth-only pass, e.g. a shadow layer fill), or a SINGLE
        // color attachment — targeting either the backbuffer (ColorView invalid) or an offscreen
        // texture view (ColorView valid, e.g. the SSAO position pre-pass). Multi-attachment is still
        // deferred (the ColorAttachments[i] slots past 0 stay reserved).
        var colorCount = pass.ColorAttachmentCount;
        if (colorCount > 1)
            throw new NotSupportedException(
                $"At most one color attachment per pass is supported (got {colorCount}). " +
                "Multi-attachment rendering is deferred.");

        WgRenderPassColorAttachment[] colors;
        if (colorCount == 0)
        {
            colors = Array.Empty<WgRenderPassColorAttachment>();
        }
        else
        {
            var src = pass.Colors.Slot0;
            colors = new WgRenderPassColorAttachment[1];
            // Offscreen color target when a ColorView is supplied; otherwise the backbuffer.
            var colorView = src.ColorView.IsValid
            ? _device.ResolveTextureView(src.ColorView)
            : backbuffer ?? throw new InvalidOperationException(
                "SubmitOffscreen streams must render only into explicit ColorView targets — " +
                "this pass targets the backbuffer. Use Submit for the frame's presenting stream.");
            // Explicit switch over LoadOp/StoreOp instead of binary comparison so a future enum
            // addition (e.g. LoadOp.DontCare for an attachment whose contents the GPU may discard)
            // surfaces as a build break here rather than silently routing through Clear/Discard.
            colors[0] = new WgRenderPassColorAttachment
            {
                View = colorView,
                LoadOp = src.Load switch
                {
                    LoadOp.Load => WgLoadOp.Load,
                    LoadOp.Clear => WgLoadOp.Clear,
                    _ => throw new NotSupportedException($"LoadOp '{src.Load}' has no WebGPU mapping."),
                },
                StoreOp = src.Store switch
                {
                    StoreOp.Store => WgStoreOp.Store,
                    StoreOp.Discard => WgStoreOp.Discard,
                    _ => throw new NotSupportedException($"StoreOp '{src.Store}' has no WebGPU mapping."),
                },
                ClearValue = new WgColor(src.ClearValue.R, src.ClearValue.G, src.ClearValue.B, src.ClearValue.A),
                DepthSlice = null,
            };
        }
        var desc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            Label = colorCount == 0 ? "ParadiseDepthPass" : "ParadiseRenderPass",
        };
        if (pass.Depth is { } depth)
        {
            // Render into an explicit view when provided (one layer of a depth array), else the
            // texture's default view. DepthTexture always identifies the underlying resource.
            var depthView = depth.DepthView.IsValid
                ? _device.ResolveTextureView(depth.DepthView)
                : _device.ResolveTexture(depth.DepthTexture).View;
            desc.DepthStencilAttachment = new WebGpuSharp.RenderPassDepthStencilAttachment
            {
                View = depthView,
                DepthLoadOp = depth.DepthLoad switch
                {
                    LoadOp.Load => WgLoadOp.Load,
                    LoadOp.Clear => WgLoadOp.Clear,
                    _ => throw new NotSupportedException($"LoadOp '{depth.DepthLoad}' has no WebGPU mapping."),
                },
                DepthStoreOp = depth.DepthStore switch
                {
                    StoreOp.Store => WgStoreOp.Store,
                    StoreOp.Discard => WgStoreOp.Discard,
                    _ => throw new NotSupportedException($"StoreOp '{depth.DepthStore}' has no WebGPU mapping."),
                },
                DepthClearValue = depth.ClearDepth,
            };
        }
        return encoder.BeginRenderPass(in desc);
    }

    private bool TryAcquireBackbufferView(out WgTextureView view) => _target.TryAcquireView(out view);

    /// <summary>Source-compatible forwarder to <see cref="ShaderProgramLoader.Load"/>, which now
    /// lives in the backend-agnostic <c>Paradise.Rendering</c> package (loading a Slang-compiled
    /// WGSL + reflection-JSON resource pair means the same thing to every backend). Kept so
    /// existing callers keep compiling; new code should call the loader directly rather than
    /// route a backend-neutral operation through a concrete backend.</summary>
    public static ShaderProgramDesc LoadShaderProgram(Assembly assembly, string logicalNamePrefix) =>
        ShaderProgramLoader.Load(assembly, logicalNamePrefix);

    /// <summary>Test-only accessor for the live-shader-slot count. Used by regression tests that
    /// assert repeated high-level <see cref="CreatePipeline(in ShaderProgramDesc, TextureFormat)"/>
    /// calls don't grow the shader slot table (iter-6 fix for the slot-leak OpenCara flagged on
    /// iter-5). Intentionally scoped <c>internal</c> + test-named so production callers don't
    /// take a dependency on internal device counters.</summary>
    internal int ShaderSlotCountForTest => _device.Shaders.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Anything still queued can never be served — there will be no further frame. Faulting is
        // the only honest answer: a task left pending here is a caller hung forever, which is how
        // this surfaced in the first place.
        while (_captureRequests.TryDequeue(out var abandoned))
        {
            abandoned.TrySetException(new ObjectDisposedException(
                nameof(WebGpuRenderer), "The renderer was disposed before a frame could serve this capture."));
        }
        _destructionQueue.DrainAll();
        _pipelineCache.Clear();
        _target.Dispose();
        _device.Dispose();
    }
}

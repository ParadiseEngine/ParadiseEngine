using System;
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
using WgCommandEncoder = WebGpuSharp.CommandEncoder;

namespace Paradise.Rendering.WebGPU;

/// <summary>WebGPU (Dawn) backend entry point. Constructed with a <see cref="SurfaceDescriptor"/>
/// for windowed rendering, or via <see cref="CreateHeadless"/> for the offscreen adapter path
/// used by CI smoke tests. Exposes resource Create/Destroy plus the
/// <see cref="Submit(in RenderCommandStream)"/> path that drives a real frame.</summary>
public sealed class WebGpuRenderer : IDisposable
{
    private const int DefaultFramesInFlight = 2;

    private readonly WebGpuDevice _device;
    private readonly SurfaceState? _surface;
    private readonly bool _isHeadless;
    private readonly DeferredDestructionQueue _destructionQueue = new(DefaultFramesInFlight);
    private readonly PipelineCache _pipelineCache = new();
    // Pipeline ↔ pass depth compatibility is a Dawn validation error (async, via the uncaptured
    // -error callback) — this side table lets Submit surface the mismatch as a synchronous,
    // descriptive exception at SetPipeline time instead. Keyed by public handle; entries follow
    // the handle's lifetime.
    private readonly System.Collections.Generic.Dictionary<PipelineHandle, bool> _pipelineHasDepth = new();
    private WgTexture? _offscreenTarget;
    private uint _offscreenWidth;
    private uint _offscreenHeight;
    private bool _disposed;

    /// <summary>Create a surface-backed renderer. The descriptor's platform tag selects the native
    /// surface variant; <see cref="SurfacePlatform.Headless"/> is rejected — use
    /// <see cref="CreateHeadless"/> instead.</summary>
    public WebGpuRenderer(in SurfaceDescriptor surface)
    {
        if (surface.Platform == SurfacePlatform.Headless)
            throw new ArgumentException(
                "Headless platform must use WebGpuRenderer.CreateHeadless(); the surface ctor requires a real native window.",
                nameof(surface));

        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        var nativeSurface = SurfaceFactory.Create(instance, surface);
        _device = WebGpuDevice.Create(instance, nativeSurface);
        _surface = new SurfaceState(_device, nativeSurface, surface.Width, surface.Height);
        _isHeadless = false;
    }

    private WebGpuRenderer(uint width, uint height)
    {
        var instance = WgWebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null — Dawn natives may be missing.");
        _device = WebGpuDevice.Create(instance, compatibleSurface: null);
        _isHeadless = true;
        _offscreenWidth = width == 0 ? 1 : width;
        _offscreenHeight = height == 0 ? 1 : height;
        _offscreenTarget = CreateOffscreenTarget(_offscreenWidth, _offscreenHeight);
    }

    /// <summary>Construct the renderer using the headless adapter path. No native surface is
    /// created; clear frames render into an offscreen <c>BGRA8Unorm</c> texture sized
    /// <paramref name="width"/> x <paramref name="height"/>. The CI smoke test driver consumes
    /// this path with <c>SDL_VIDEODRIVER=dummy</c>.</summary>
    public static WebGpuRenderer CreateHeadless(uint width = 1, uint height = 1) => new(width, height);

    /// <summary>The native swapchain format for windowed renderers, or <see cref="TextureFormat.Bgra8Unorm"/>
    /// for the headless offscreen target. Pipeline color targets must match this format or the
    /// backend will reject the pipeline at draw time.</summary>
    public TextureFormat ColorFormat =>
        _isHeadless
            ? TextureFormat.Bgra8Unorm
            : FormatConversions.FromWgpu(_surface!.Format);

    /// <summary>Resize the surface (or offscreen target) to <paramref name="width"/> x
    /// <paramref name="height"/>. Zero-sized requests are clamped to 1.</summary>
    public void Resize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isHeadless)
        {
            if (width == 0) width = 1;
            if (height == 0) height = 1;
            if (width == _offscreenWidth && height == _offscreenHeight) return;
            _offscreenTarget?.Destroy();
            _offscreenWidth = width;
            _offscreenHeight = height;
            _offscreenTarget = CreateOffscreenTarget(width, height);
        }
        else
        {
            _surface!.Resize(width, height);
        }
    }

    /// <summary>Acquire the next color attachment, run a single render pass that clears it to
    /// <paramref name="clearColor"/>, submit, and present (windowed) or simply discard (headless).</summary>
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
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        if (!_isHeadless) _surface!.Native.Present();
        _destructionQueue.AdvanceFrame();
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
        string? fragmentEntryPoint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? vsModule = null;
        ShaderModuleDesc? fsModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Vertex) != 0) vsModule = m;
            if ((m.Stage & ShaderStage.Fragment) != 0)
            {
                // Multi-fragment-entry programs (e.g. linear vs sRGB-encoding variants) select by
                // name; without a selector the first fragment module wins.
                if (fragmentEntryPoint is null && fsModule is null) fsModule = m;
                else if (fragmentEntryPoint is not null && string.Equals(m.EntryPoint, fragmentEntryPoint, StringComparison.Ordinal)) fsModule = m;
            }
        }
        if (vsModule is null) throw new InvalidOperationException("ShaderProgramDesc has no vertex module.");
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
                VertexLayouts = program.VertexBuffers,
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
        CompareFunction depthCompare = CompareFunction.Less)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShaderModuleDesc? vsModule = null;
        foreach (var m in program.Modules)
            if ((m.Stage & ShaderStage.Vertex) != 0) vsModule = m;
        if (vsModule is null) throw new InvalidOperationException("Depth-only program has no vertex module.");

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

    // -------- Command stream submission --------

    /// <summary>Submit a recorded <see cref="RenderCommandStream"/>. Acquires the backbuffer view,
    /// walks every <see cref="RenderCommand"/>, dispatches to WebGPU, presents (when windowed),
    /// and advances the frame counter so deferred destructions can drain.</summary>
    /// <summary>Optional overlay pass recorded into the frame encoder AFTER the scene passes and
    /// before submit/present — the seam UI composition (e.g. the Noesis device) hooks into. The
    /// callback receives the frame's command encoder and the backbuffer view; passes it records
    /// should load (not clear) the color target so they composite over the scene. Invoked on the
    /// render thread only.</summary>
    public Action<WebGpuSharp.CommandEncoder, WgTextureView>? OverlayPass { get; set; }

    /// <summary>The raw WebGPUSharp device, for subsystems that record their own passes through
    /// <see cref="OverlayPass"/> (they need it to create pipelines/buffers/textures). Treat as
    /// read-only infrastructure — resource lifetime stays with the creating subsystem.</summary>
    public WebGpuSharp.Device NativeDevice => _device.Device;

    public void Submit(in RenderCommandStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        ExecuteStream(in stream, encoder, view);
        OverlayPass?.Invoke(encoder, view);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        if (!_isHeadless) _surface!.Native.Present();
        _destructionQueue.AdvanceFrame();
    }

    /// <summary>Read the headless offscreen color target back to CPU memory as tightly-packed,
    /// top-down <c>BGRA8</c> (4 bytes/pixel, <see cref="ColorFormat"/> = <see cref="TextureFormat.Bgra8Unorm"/>).
    /// Blocks on GPU completion — intended for screenshots and image-based tests, not per-frame use.
    /// Headless renderers only (windowed swapchain textures aren't CopySrc).</summary>
    public byte[] ReadbackColor(out uint width, out uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isHeadless || _offscreenTarget is null)
            throw new InvalidOperationException(
                "ReadbackColor is only available on a headless renderer (CreateHeadless).");

        width = _offscreenWidth;
        height = _offscreenHeight;
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
        var source = new WebGpuSharp.TexelCopyTextureInfo { Texture = _offscreenTarget, MipLevel = 0 };
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

    private void ExecuteStream(in RenderCommandStream stream, WgCommandEncoder encoder, WgTextureView backbuffer)
    {
        var passes = stream.Passes.Span;
        var commands = stream.Commands.Span;

        WgRenderPassEncoder? activePass = null;
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
                        var pass = RequireActivePass(activePass);
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
                        var pass = RequireActivePass(activePass);
                        var p = cmd.SetVertexBuffer;
                        pass.SetVertexBuffer(p.Slot, _device.ResolveBuffer(p.Buffer), p.Offset, p.Size);
                        break;
                    }
                    case RenderCommandKind.SetIndexBuffer:
                    {
                        var pass = RequireActivePass(activePass);
                        var p = cmd.SetIndexBuffer;
                        pass.SetIndexBuffer(_device.ResolveBuffer(p.Buffer), FormatConversions.ToWgpu(p.Format), p.Offset, p.Size);
                        break;
                    }
                    case RenderCommandKind.SetBindGroup:
                    {
                        var pass = RequireActivePass(activePass);
                        var p = cmd.SetBindGroup;
                        var native = _device.ResolveBindGroup(p.Group);
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
                        var pass = RequireActivePass(activePass);
                        var d = cmd.Draw;
                        pass.Draw(d.VertexCount, d.InstanceCount, d.FirstVertex, d.FirstInstance);
                        break;
                    }
                    case RenderCommandKind.DrawIndexed:
                    {
                        var pass = RequireActivePass(activePass);
                        var d = cmd.DrawIndexed;
                        pass.DrawIndexed(d.IndexCount, d.InstanceCount, d.FirstIndex, d.BaseVertex, d.FirstInstance);
                        break;
                    }
                    case RenderCommandKind.SetViewport:
                    {
                        var pass = RequireActivePass(activePass);
                        var v = cmd.SetViewport;
                        pass.SetViewport((uint)v.X, (uint)v.Y, (uint)v.Width, (uint)v.Height, v.MinDepth, v.MaxDepth);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unknown RenderCommandKind '{cmd.Kind}'.");
                }
            }

            if (activePass is not null)
                throw new InvalidOperationException("RenderCommandStream ended with an open render pass — missing EndPass.");
            activePass = null;
        }
        finally
        {
            // Defensive close on exception so a stale-handle / NotSupportedException mid-pass
            // doesn't leave the native render-pass encoder un-Ended (Dawn requires every
            // BeginRenderPass to be matched with End before the encoder is finished). On the
            // happy path activePass was nulled before the try-block exited.
            activePass?.End();
        }
    }

    private static WgRenderPassEncoder RequireActivePass(WgRenderPassEncoder? pass) =>
        pass ?? throw new InvalidOperationException("Render command issued outside of an active BeginPass/EndPass scope.");

    private WgRenderPassEncoder BeginPass(WgCommandEncoder encoder, RenderPassDesc pass, WgTextureView backbuffer)
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
            var colorView = src.ColorView.IsValid ? _device.ResolveTextureView(src.ColorView) : backbuffer;
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

    private bool TryAcquireBackbufferView(out WgTextureView view)
    {
        if (_isHeadless)
        {
            view = _offscreenTarget!.CreateView();
            return true;
        }

        var current = _surface!.Native.GetCurrentTexture();
        switch (current.Status)
        {
            case WgSurfaceGetCurrentTextureStatus.SuccessOptimal:
            case WgSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                break;
            case WgSurfaceGetCurrentTextureStatus.Outdated:
            case WgSurfaceGetCurrentTextureStatus.Lost:
                _surface.Reconfigure();
                view = null!;
                return false;
            default:
                throw new InvalidOperationException($"Surface texture acquisition failed: {current.Status}");
        }
        var surfaceTexture = current.Texture
            ?? throw new InvalidOperationException(
                $"Surface texture was null despite status {current.Status} — WebGPUSharp invariant violation.");
        view = surfaceTexture.CreateView();
        return true;
    }

    private WgTexture CreateOffscreenTarget(uint width, uint height)
    {
        var desc = new WgTextureDescriptor
        {
            Label = "ParadiseHeadlessTarget",
            Size = new WgExtent3D(width, height, 1),
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = WgTextureDimension.D2,
            Format = WgTextureFormat.BGRA8Unorm,
            Usage = WgTextureUsage.RenderAttachment | WgTextureUsage.CopySrc,
        };
        return _device.Device.CreateTexture(in desc);
    }

    /// <summary>Internal helper for <see cref="Paradise.Rendering.Sample"/>: load a Slang-compiled
    /// shader pair from an embedded resource pair (<paramref name="logicalNamePrefix"/>.wgsl +
    /// .reflection.json) and return a <see cref="ShaderProgramDesc"/>. Wraps
    /// <see cref="ShaderProgramLoader.Load"/> so the loader stays internal while the sample
    /// (and tests) can drive it without InternalsVisibleTo to every consumer.</summary>
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
        _destructionQueue.DrainAll();
        _pipelineCache.Clear();
        _offscreenTarget?.Destroy();
        _surface?.Dispose();
        _device.Dispose();
    }
}

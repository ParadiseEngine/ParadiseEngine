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
using WgRenderPassDepthStencilAttachment = WebGpuSharp.RenderPassDepthStencilAttachment;
using WgLoadOp = WebGpuSharp.LoadOp;
using WgStoreOp = WebGpuSharp.StoreOp;
using WgColor = WebGpuSharp.Color;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgSurfaceGetCurrentTextureStatus = WebGpuSharp.SurfaceGetCurrentTextureStatus;
using WgRenderPassEncoder = WebGpuSharp.RenderPassEncoder;
using WgCommandEncoder = WebGpuSharp.CommandEncoder;
using WgBindGroupLayout = WebGpuSharp.BindGroupLayout;
using WgBindGroupLayoutDescriptor = WebGpuSharp.BindGroupLayoutDescriptor;
using WgBindGroupLayoutEntry = WebGpuSharp.BindGroupLayoutEntry;
using WgBufferBindingLayout = WebGpuSharp.BufferBindingLayout;
using WgSamplerBindingLayout = WebGpuSharp.SamplerBindingLayout;
using WgTextureBindingLayout = WebGpuSharp.TextureBindingLayout;
using WgBufferBindingType = WebGpuSharp.BufferBindingType;
using WgSamplerBindingType = WebGpuSharp.SamplerBindingType;
using WgTextureSampleType = WebGpuSharp.TextureSampleType;
using WgTextureViewDimension = WebGpuSharp.TextureViewDimension;

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
    private readonly BindGroupLayoutCache _bindGroupLayoutCache = new();
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

    public PipelineHandle CreatePipeline(in PipelineDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Cache the native WebGPU pipeline below the public handle layer so two calls with the
        // same content share the GPU compile, but each caller gets a distinct PipelineHandle.
        // First DestroyPipeline doesn't invalidate the second handle — matches the contract of
        // every other resource type (BufferHandle, TextureHandle, ShaderHandle).
        var native = _pipelineCache.GetOrCreateNative(in desc, d => _device.BuildNativePipeline(in d));
        return _device.RegisterPipeline(native);
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
        IndexFormat stripIndexFormat = IndexFormat.Uint16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CreatePipelineInternal(
            in program,
            colorFormat,
            depthStencilFormat: null,
            depthStencil: null,
            bindGroupLayouts: ReadOnlyMemory<BindGroupLayoutHandle>.Empty,
            layoutOverride: null,
            topology: topology,
            stripIndexFormat: stripIndexFormat);
    }

    /// <summary>Overload that accepts a caller-authored <see cref="PipelineDesc"/> template whose
    /// pipeline layout, bind group layouts, depth state, and color format override the defaults.
    /// The shader module fields on the template are ignored — the loader derives them from
    /// <paramref name="program"/> — so callers can build a single template that names the
    /// non-shader pipeline parameters and reuse it across shader permutations.
    /// <see cref="PipelineDesc.Layout"/>, when non-null, overrides
    /// <see cref="ShaderProgramDesc.Layout"/>; pass <c>null</c> to use the program's reflection-
    /// derived layout. <see cref="PipelineDesc.VertexLayouts"/> is always taken from
    /// <paramref name="program"/>.</summary>
    public PipelineHandle CreatePipeline(in ShaderProgramDesc program, in PipelineDesc template)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CreatePipelineInternal(
            in program,
            template.ColorFormat,
            depthStencilFormat: template.DepthStencilFormat,
            depthStencil: template.DepthStencil,
            bindGroupLayouts: template.BindGroupLayouts,
            layoutOverride: template.Layout,
            topology: template.Topology,
            stripIndexFormat: template.StripIndexFormat,
            label: template.Name);
    }

    private PipelineHandle CreatePipelineInternal(
        in ShaderProgramDesc program,
        TextureFormat colorFormat,
        TextureFormat? depthStencilFormat,
        DepthStencilState? depthStencil,
        ReadOnlyMemory<BindGroupLayoutHandle> bindGroupLayouts,
        PipelineLayoutDesc? layoutOverride,
        PrimitiveTopology topology,
        IndexFormat stripIndexFormat,
        string? label = null)
    {
        ShaderModuleDesc? vsModule = null;
        ShaderModuleDesc? fsModule = null;
        foreach (var m in program.Modules)
        {
            if ((m.Stage & ShaderStage.Vertex) != 0) vsModule = m;
            if ((m.Stage & ShaderStage.Fragment) != 0) fsModule = m;
        }
        if (vsModule is null) throw new InvalidOperationException("ShaderProgramDesc has no vertex module.");
        if (fsModule is null) throw new InvalidOperationException("ShaderProgramDesc has no fragment module.");

        // CreateShaderModule dedupes the underlying native WgShaderModule by (Wgsl, EntryPoint,
        // Stage) inside _shaderModuleCache, but mints a FRESH ShaderHandle per call. These two
        // handles are consumed locally by the PipelineDesc below and never reach the caller, so
        // we must destroy them after CreatePipeline(in pipelineDesc) returns — otherwise every
        // call leaks two _device.Shaders slot entries for the renderer's lifetime (the native
        // module is safe — the content cache AND the native WgRenderPipeline both retain it).
        ShaderHandle vsHandle = default;
        ShaderHandle fsHandle = default;
        try
        {
            vsHandle = _device.CreateShaderModule(vsModule);
            fsHandle = _device.CreateShaderModule(fsModule);

            var pipelineDesc = new PipelineDesc
            {
                Name = label ?? "ShaderProgramPipeline",
                VertexShader = vsHandle,
                VertexEntryPoint = vsModule.EntryPoint,
                FragmentShader = fsHandle,
                FragmentEntryPoint = fsModule.EntryPoint,
                VertexLayouts = program.VertexBuffers,
                Topology = topology,
                StripIndexFormat = stripIndexFormat,
                ColorFormat = colorFormat,
                DepthStencilFormat = depthStencilFormat,
                DepthStencil = depthStencil,
                Layout = layoutOverride ?? program.Layout,
                BindGroupLayouts = bindGroupLayouts,
            };
            return CreatePipeline(in pipelineDesc);
        }
        finally
        {
            if (vsHandle.IsValid) DestroyShader(vsHandle);
            if (fsHandle.IsValid) DestroyShader(fsHandle);
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
    }

    // -------- M2: textures, views, samplers, bind groups --------

    /// <summary>Create an empty GPU texture. Storage only — upload via
    /// <see cref="CreateTextureWithData{T}"/> or a command encoder copy.</summary>
    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateTexture(in desc);
    }

    /// <summary>Create a 2D texture and populate mip level 0 / layer 0 from <paramref name="pixels"/>.
    /// Adds <see cref="TextureUsage.CopyDst"/> to the usage flags implicitly so the upload can
    /// succeed even when the caller didn't include it.</summary>
    public TextureHandle CreateTextureWithData<T>(in TextureDesc desc, ReadOnlySpan<T> pixels) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (desc.Dimension != TextureDimension.D2)
            throw new NotSupportedException(
                "Paradise.Rendering M2 only supports CreateTextureWithData for 2D textures; 1D/3D uploads are reserved for a later milestone.");
        var sized = desc with { Usage = desc.Usage | TextureUsage.CopyDst };
        var handle = _device.CreateTexture(in sized);
        var bpp = BytesPerPixel(desc.Format);
        _device.WriteTexture2D(handle, desc.Width, desc.Height, bpp, pixels);
        return handle;
    }

    private static uint BytesPerPixel(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => 1,
        TextureFormat.Rgba8Unorm => 4,
        TextureFormat.Rgba8UnormSrgb => 4,
        TextureFormat.Bgra8Unorm => 4,
        TextureFormat.Bgra8UnormSrgb => 4,
        _ => throw new NotSupportedException(
            $"Paradise.Rendering M2 does not yet support CreateTextureWithData for texture format '{format}'; reserved for later milestone."),
    };

    public void DestroyTexture(TextureHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.DetachTexture(handle, out var native))
            return;
        // Defer the native destroy so in-flight GPU work referencing the texture finishes first.
        _destructionQueue.Schedule(() => native.Destroy());
    }

    public RenderViewHandle CreateTextureView(TextureHandle texture, in RenderViewDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.CreateTextureView(texture, in desc);
    }

    public void DestroyTextureView(RenderViewHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // TextureView has no native Destroy() in WebGPUSharp — it's released via GC. Slot
        // invalidation is synchronous; the captured native reference keeps the view alive for the
        // deferred frame window so in-flight GPU work finishes.
        if (!_device.DetachTextureView(handle, out var native))
            return;
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
        _destructionQueue.Schedule(() => { _ = native; });
    }

    /// <summary>Create a bind group layout. The record's structural content is content-keyed in a
    /// cache below the public handle layer, so two calls with structurally-equal
    /// <see cref="BindGroupLayoutDesc"/> values share the native Dawn layout while each caller
    /// gets a distinct handle — matches the pipeline cache contract.</summary>
    public BindGroupLayoutHandle CreateBindGroupLayout(BindGroupLayoutDesc layoutDesc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var native = _bindGroupLayoutCache.GetOrCreateNative(layoutDesc, BuildNativeBindGroupLayout);
        return _device.CreateBindGroupLayout(native);
    }

    /// <summary>Test/inspection accessor for the content-keyed layout cache's entry count. Scoped
    /// <c>internal</c> so tests can verify cache hits without production callers depending on
    /// implementation details.</summary>
    internal int BindGroupLayoutCacheCountForTest => _bindGroupLayoutCache.Count;

    public void DestroyBindGroupLayout(BindGroupLayoutHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Mirrors DestroyPipeline: native layout is cache-owned, slot invalidation is synchronous.
        _device.DetachBindGroupLayout(handle);
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

    /// <summary>Upload <paramref name="data"/> to an existing buffer at <paramref name="offset"/>
    /// via <c>Queue.WriteBuffer</c>. The buffer's creation usage must include
    /// <see cref="BufferUsage.CopyDst"/>; the backend does not rewrite usage here.</summary>
    public void WriteBuffer<T>(BufferHandle buffer, ulong offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var native = _device.ResolveBuffer(buffer);
        _device.Queue.WriteBuffer(native, offset, data);
    }

    private WgBindGroupLayout BuildNativeBindGroupLayout(BindGroupLayoutDesc layoutDesc)
    {
        var entries = new WgBindGroupLayoutEntry[layoutDesc.Entries.Length];
        for (var i = 0; i < layoutDesc.Entries.Length; i++)
        {
            var e = layoutDesc.Entries[i];
            var entry = new WgBindGroupLayoutEntry
            {
                Binding = e.Binding,
                Visibility = FormatConversions.ToWgpu(e.Visibility),
            };
            switch (e.Type)
            {
                case BindingResourceType.UniformBuffer:
                    entry.Buffer = new WgBufferBindingLayout { Type = WgBufferBindingType.Uniform, MinBindingSize = e.MinBufferSize };
                    break;
                case BindingResourceType.StorageBuffer:
                    entry.Buffer = new WgBufferBindingLayout { Type = WgBufferBindingType.Storage, MinBindingSize = e.MinBufferSize };
                    break;
                case BindingResourceType.ReadonlyStorageBuffer:
                    entry.Buffer = new WgBufferBindingLayout { Type = WgBufferBindingType.ReadOnlyStorage, MinBindingSize = e.MinBufferSize };
                    break;
                case BindingResourceType.Sampler:
                    entry.Sampler = new WgSamplerBindingLayout { Type = WgSamplerBindingType.Filtering };
                    break;
                case BindingResourceType.ComparisonSampler:
                    entry.Sampler = new WgSamplerBindingLayout { Type = WgSamplerBindingType.Comparison };
                    break;
                case BindingResourceType.SampledTexture:
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.Float,
                        ViewDimension = WgTextureViewDimension.D2,
                        Multisampled = false,
                    };
                    break;
                case BindingResourceType.MultisampledTexture:
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.Float,
                        ViewDimension = WgTextureViewDimension.D2,
                        Multisampled = true,
                    };
                    break;
                case BindingResourceType.StorageTexture:
                    throw new NotSupportedException(
                        "Paradise.Rendering M2 does not yet support StorageTexture bindings; reserved for a later milestone.");
                default:
                    throw new NotSupportedException($"Unknown BindingResourceType '{e.Type}'.");
            }
            entries[i] = entry;
        }
        var layoutWgDesc = new WgBindGroupLayoutDescriptor
        {
            Label = string.Empty,
            Entries = entries,
        };
        return _device.Device.CreateBindGroupLayout(in layoutWgDesc)
            ?? throw new InvalidOperationException("BindGroupLayout creation returned null.");
    }

    // -------- Command stream submission --------

    /// <summary>Submit a recorded <see cref="RenderCommandStream"/>. Acquires the backbuffer view,
    /// walks every <see cref="RenderCommand"/>, dispatches to WebGPU, presents (when windowed),
    /// and advances the frame counter so deferred destructions can drain.</summary>
    public void Submit(in RenderCommandStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryAcquireBackbufferView(out var view)) return;

        var encoder = _device.Device.CreateCommandEncoder();
        ExecuteStream(in stream, encoder, view);
        var commandBuffer = encoder.Finish();
        _device.Queue.Submit(commandBuffer);
        if (!_isHeadless) _surface!.Native.Present();
        _destructionQueue.AdvanceFrame();
    }

    private void ExecuteStream(in RenderCommandStream stream, WgCommandEncoder encoder, WgTextureView backbuffer)
    {
        var passes = stream.Passes.Span;
        var commands = stream.Commands.Span;
        var dynamicOffsets = stream.DynamicOffsets.Span;

        WgRenderPassEncoder? activePass = null;
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
                        pass.SetPipeline(_device.ResolvePipeline(cmd.SetPipeline.Pipeline));
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
                        var bindGroup = _device.ResolveBindGroup(p.BindGroup);
                        if (p.DynamicOffsetsCount == 0)
                        {
                            pass.SetBindGroup(p.GroupIndex, bindGroup);
                        }
                        else
                        {
                            var start = (int)p.DynamicOffsetsStart;
                            var count = (int)p.DynamicOffsetsCount;
                            if ((uint)start + (uint)count > (uint)dynamicOffsets.Length)
                                throw new InvalidOperationException(
                                    $"SetBindGroup dynamic-offsets range [{start}, {start + count}) exceeds stream.DynamicOffsets length {dynamicOffsets.Length}.");
                            pass.SetBindGroup(p.GroupIndex, bindGroup, dynamicOffsets.Slice(start, count));
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
        // M2 still supports a single color attachment that always targets the backbuffer view;
        // multi-attachment passes land in a later milestone. Depth IS now honored.
        var colorCount = pass.ColorAttachmentCount;
        if (colorCount != 1)
            throw new NotSupportedException(
                $"Paradise.Rendering M2 supports exactly one color attachment per pass (got {colorCount}). " +
                "Multi-attachment rendering is reserved for a later milestone.");

        var src = pass.Colors.Slot0;
        var colors = new WgRenderPassColorAttachment[1];
        colors[0] = new WgRenderPassColorAttachment
        {
            View = backbuffer,
            LoadOp = FormatConversions.ToWgpu(src.Load),
            StoreOp = FormatConversions.ToWgpu(src.Store),
            ClearValue = new WgColor(src.ClearValue.R, src.ClearValue.G, src.ClearValue.B, src.ClearValue.A),
            DepthSlice = null,
        };

        WgRenderPassDepthStencilAttachment? depthAttachment = null;
        if (pass.Depth is { } d)
        {
            var depthTexture = _device.ResolveTexture(d.DepthTexture);
            // Create a view for the depth texture each frame. Cheap — the view is a WebGPUSharp
            // wrapper, not a heavyweight resource — and avoids holding a RenderViewHandle just for
            // this one internal consumer. If a future milestone exposes per-pass depth view reuse
            // (different mip/layer per frame), this grows into a small cache keyed by texture.
            var depthView = depthTexture.CreateView()
                ?? throw new InvalidOperationException("Depth texture CreateView returned null.");
            depthAttachment = new WgRenderPassDepthStencilAttachment
            {
                View = depthView,
                DepthLoadOp = FormatConversions.ToWgpu(d.DepthLoad),
                DepthStoreOp = FormatConversions.ToWgpu(d.DepthStore),
                DepthClearValue = d.ClearDepth,
                DepthReadOnly = false,
                // Stencil defaults: M2 does not author stencil state and only supports
                // depth-only formats (Depth32Float). The WebGPU spec requires stencil load/store
                // ops to be UNSET on depth-only formats — `WgLoadOp.Undefined` / `WgStoreOp.Undefined`
                // map to "unset" and are correct here. For combined formats like
                // Depth24PlusStencil8, valid stencil ops would be REQUIRED instead and Dawn would
                // reject Undefined; combined formats are not yet supported (the Format guard
                // upstream rejects pipelines that mix them with this attachment).
                StencilLoadOp = WgLoadOp.Undefined,
                StencilStoreOp = WgStoreOp.Undefined,
                StencilClearValue = 0,
                StencilReadOnly = false,
            };
        }

        var desc = new WgRenderPassDescriptor
        {
            ColorAttachments = colors,
            DepthStencilAttachment = depthAttachment,
            Label = "ParadiseRenderPass",
        };
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
        _bindGroupLayoutCache.Clear();
        _offscreenTarget?.Destroy();
        _surface?.Dispose();
        _device.Dispose();
    }
}

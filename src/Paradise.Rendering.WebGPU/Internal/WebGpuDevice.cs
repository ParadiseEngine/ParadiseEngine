using System;
using WgInstance = WebGpuSharp.Instance;
using WgAdapter = WebGpuSharp.Adapter;
using WgDevice = WebGpuSharp.Device;
using WgQueue = WebGpuSharp.Queue;
using WgSurface = WebGpuSharp.Surface;
using WgRequestAdapterOptions = WebGpuSharp.RequestAdapterOptions;
using WgDeviceDescriptor = WebGpuSharp.DeviceDescriptor;
using WgPowerPreference = WebGpuSharp.PowerPreference;
using WgFeatureLevel = WebGpuSharp.FeatureLevel;
using WgShaderModule = WebGpuSharp.ShaderModule;
using WgShaderModuleWGSLDescriptor = WebGpuSharp.ShaderModuleWGSLDescriptor;
using WgBuffer = WebGpuSharp.Buffer;
using WgBufferDescriptor = WebGpuSharp.BufferDescriptor;
using WgRenderPipeline = WebGpuSharp.RenderPipeline;
using WgRenderPipelineDescriptor = WebGpuSharp.RenderPipelineDescriptor;
using WgVertexState = WebGpuSharp.VertexState;
using WgVertexBufferLayout = WebGpuSharp.VertexBufferLayout;
using WgVertexAttribute = WebGpuSharp.VertexAttribute;
using WgFragmentState = WebGpuSharp.FragmentState;
using WgColorTargetState = WebGpuSharp.ColorTargetState;
using WgPrimitiveState = WebGpuSharp.PrimitiveState;
using WgMultisampleState = WebGpuSharp.MultisampleState;
using WgTexture = WebGpuSharp.Texture;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgTextureView = WebGpuSharp.TextureView;
using WgTextureViewDescriptor = WebGpuSharp.TextureViewDescriptor;
using WgSampler = WebGpuSharp.Sampler;
using WgSamplerDescriptor = WebGpuSharp.SamplerDescriptor;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgOrigin3D = WebGpuSharp.Origin3D;
using WgBindGroupLayout = WebGpuSharp.BindGroupLayout;
using WgBindGroupLayoutDescriptor = WebGpuSharp.BindGroupLayoutDescriptor;
using WgBindGroupLayoutEntry = WebGpuSharp.BindGroupLayoutEntry;
using WgBufferBindingLayout = WebGpuSharp.BufferBindingLayout;
using WgSamplerBindingLayout = WebGpuSharp.SamplerBindingLayout;
using WgTextureBindingLayout = WebGpuSharp.TextureBindingLayout;
using WgBufferBindingType = WebGpuSharp.BufferBindingType;
using WgSamplerBindingType = WebGpuSharp.SamplerBindingType;
using WgTextureSampleType = WebGpuSharp.TextureSampleType;
using WgBindGroup = WebGpuSharp.BindGroup;
using WgBindGroupDescriptor = WebGpuSharp.BindGroupDescriptor;
using WgBindGroupEntry = WebGpuSharp.BindGroupEntry;
using WgPipelineLayout = WebGpuSharp.PipelineLayout;
using WgPipelineLayoutDescriptor = WebGpuSharp.PipelineLayoutDescriptor;
using WgDepthStencilState = WebGpuSharp.DepthStencilState;
using WgStencilFaceState = WebGpuSharp.StencilFaceState;
using WgCompareFunction = WebGpuSharp.CompareFunction;
using WgStencilOperation = WebGpuSharp.StencilOperation;
using WgOptionalBool = WebGpuSharp.OptionalBool;
using WgTexelCopyTextureInfo = WebGpuSharp.TexelCopyTextureInfo;
using WgTexelCopyBufferLayout = WebGpuSharp.TexelCopyBufferLayout;
using WgTextureViewDimension = WebGpuSharp.TextureViewDimension;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Owns the long-lived WebGPU instance/adapter/device/queue chain plus the slot tables
/// that map <see cref="Paradise.Rendering"/> handles to native WebGPUSharp objects. Constructed
/// once per <see cref="WebGpuRenderer"/>. Adapter selection takes an optional compatible
/// <see cref="WgSurface"/> — pass <c>null</c> to drive the headless adapter path (no swapchain).</summary>
internal sealed class WebGpuDevice : IDisposable
{
    public WgInstance Instance { get; }
    public WgAdapter Adapter { get; }
    public WgDevice Device { get; }
    public WgQueue Queue { get; }

    public SlotTable<WgShaderModule> Shaders { get; } = new();
    public SlotTable<WgBuffer> Buffers { get; } = new();
    public SlotTable<WgRenderPipeline> Pipelines { get; } = new();
    public SlotTable<WgTexture> Textures { get; } = new();
    public SlotTable<WgTextureView> TextureViews { get; } = new();
    public SlotTable<WgSampler> Samplers { get; } = new();
    public SlotTable<WgBindGroupLayout> BindGroupLayouts { get; } = new();
    public SlotTable<WgBindGroup> BindGroups { get; } = new();

    // Content-keyed native shader-module cache. Sits BELOW the public ShaderHandle layer so the
    // renderer can hand out fresh handles per CreateShaderModule call while still deduping the
    // underlying Dawn shader module by (WGSL source + entry point + stage). Mirrors
    // PipelineCache's design: insert-only, renderer-lifetime, native survives as long as the
    // renderer. Two callers that request the same shader-module content get distinct
    // ShaderHandles that both resolve; destroying one never invalidates the other — same public
    // contract as every other resource type. Revisited in M2/M3 if/when hot-reload or large
    // shader libraries make retain/release or LRU eviction worthwhile.
    private readonly System.Collections.Generic.Dictionary<ShaderModuleCacheKey, WgShaderModule> _shaderModuleCache = new();

    private readonly record struct ShaderModuleCacheKey(string Wgsl, string EntryPoint, ShaderStage Stage);

    private bool _disposed;

    private WebGpuDevice(WgInstance instance, WgAdapter adapter, WgDevice device, WgQueue queue)
    {
        Instance = instance;
        Adapter = adapter;
        Device = device;
        Queue = queue;
    }

    public static WebGpuDevice Create(WgInstance instance, WgSurface? compatibleSurface)
    {
        const ulong AdapterTimeoutNs = 10_000_000_000UL; // 10s — generous for cold first-run on CI

        // CompatibleSurface is a required init member on RequestAdapterOptions, so two literals
        // is simpler than reflection — picking the headless path means leaving it default-null.
        WgAdapter? adapter;
        if (compatibleSurface is not null)
        {
            var opts = new WgRequestAdapterOptions
            {
                CompatibleSurface = compatibleSurface,
                PowerPreference = WgPowerPreference.HighPerformance,
                FeatureLevel = WgFeatureLevel.Core,
            };
            adapter = instance.RequestAdapterSync(in opts, AdapterTimeoutNs);
        }
        else
        {
            var opts = new WgRequestAdapterOptions
            {
                CompatibleSurface = null!,
                PowerPreference = WgPowerPreference.HighPerformance,
                FeatureLevel = WgFeatureLevel.Core,
            };
            adapter = instance.RequestAdapterSync(in opts, AdapterTimeoutNs);
        }

        if (adapter is null)
            throw new AdapterUnavailableException(
                "No WebGPU adapter available. On Linux without a GPU, install mesa-vulkan-drivers / libvulkan1 (lavapipe) for headless support.");

        var deviceDesc = new WgDeviceDescriptor
        {
            Label = "Paradise.Rendering.WebGPU",
            UncapturedErrorCallback = static (type, message) =>
            {
                var text = message.Length == 0 ? "(no message)" : System.Text.Encoding.UTF8.GetString(message);
                Console.Error.WriteLine($"[WebGPU] {type}: {text}");
            },
        };

        var device = adapter.RequestDeviceSync(in deviceDesc, AdapterTimeoutNs)
            ?? throw new InvalidOperationException("WebGPU device creation failed.");

        var queue = device.GetQueue();
        return new WebGpuDevice(instance, adapter, device, queue);
    }

    /// <summary>Raw-WGSL shader creation. Each call compiles a fresh native <see cref="WgShaderModule"/>
    /// and mints a fresh public <see cref="ShaderHandle"/>. Does NOT go through the content-keyed
    /// dedupe cache — the raw path is intended for tests and consumers bringing their own WGSL
    /// strings, and skipping the cache avoids pinning the module references past the caller's
    /// DestroyShader call. Use <see cref="CreateShaderModule"/> for the cached Slang path.</summary>
    public ShaderHandle CreateShader(string wgsl, string label)
    {
        var wgslDesc = new WgShaderModuleWGSLDescriptor { Code = wgsl };
        var module = Device.CreateShaderModuleWGSL(label, in wgslDesc)
            ?? throw new InvalidOperationException("ShaderModule creation returned null.");
        var (index, generation) = Shaders.Add(module);
        return new ShaderHandle(index, generation);
    }

    public WgShaderModule ResolveShader(ShaderHandle h)
    {
        if (!Shaders.TryGet(h.Index, h.Generation, out var module))
            throw new StaleHandleException($"Shader handle ({h.Index},{h.Generation}) is stale or invalid.");
        return module;
    }

    /// <summary>Synchronously invalidate the slot for <paramref name="h"/> and hand the slot's
    /// native-module reference back to the caller. Does NOT touch the content-keyed
    /// <c>_shaderModuleCache</c>: multiple ShaderHandles can resolve to the same cached native
    /// module, so destroying one handle must not yank the native out from under the others.
    /// After this returns, <see cref="ResolveShader"/> on <paramref name="h"/> throws
    /// <see cref="StaleHandleException"/>.</summary>
    public bool DetachShader(ShaderHandle h, out WgShaderModule native) =>
        Shaders.Detach(h.Index, h.Generation, out native);

    public bool TryResolveShader(ShaderHandle h, out WgShaderModule module) =>
        Shaders.TryGet(h.Index, h.Generation, out module);

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        // MappedAtCreation is intentionally unset — M1's public buffer surface is
        // CreateBuffer + CreateBufferWithData (the latter initialises via Queue.WriteBuffer), and
        // there is no public map/unmap API to pair with a mapped-at-creation flag. Exposing the
        // flag without a map/unmap path was the iteration-3 defect.
        var bd = new WgBufferDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Size = desc.Size,
            Usage = FormatConversions.ToWgpu(desc.Usage),
        };
        var buffer = Device.CreateBuffer(ref bd)
            ?? throw new InvalidOperationException("Buffer creation returned null.");
        var (index, generation) = Buffers.Add(buffer);
        return new BufferHandle(index, generation);
    }

    public WgBuffer ResolveBuffer(BufferHandle h)
    {
        if (!Buffers.TryGet(h.Index, h.Generation, out var buffer))
            throw new StaleHandleException($"Buffer handle ({h.Index},{h.Generation}) is stale or invalid.");
        return buffer;
    }

    /// <summary>Synchronously invalidate the slot for <paramref name="h"/> and hand the native
    /// buffer back to the caller. The native <c>Destroy()</c> is NOT called here — the caller
    /// schedules it on the deferred-destruction queue so in-flight GPU work finishes safely. After
    /// this returns, <see cref="ResolveBuffer"/> on <paramref name="h"/> throws
    /// <see cref="StaleHandleException"/>.</summary>
    public bool DetachBuffer(BufferHandle h, out WgBuffer native) =>
        Buffers.Detach(h.Index, h.Generation, out native);

    /// <summary>Build a native WebGPU pipeline from <paramref name="desc"/> without allocating a
    /// slot-table entry. Used by <see cref="WebGpuRenderer.CreatePipeline"/> in conjunction with
    /// the cache + <see cref="RegisterPipeline"/>: the cache stores the native pipeline once per
    /// content hash, every CreatePipeline call mints its own public handle pointing at the
    /// shared native pipeline.</summary>
    public WgRenderPipeline BuildNativePipeline(in PipelineDesc desc)
    {
        // M2 still reserves push constants for a later milestone — Dawn exposes them via
        // chained structs only. Depth/stencil and bind group layouts land here.
        if (desc.Layout is { } l && l.PushConstants.Length > 0)
            throw new NotSupportedException(
                "Paradise.Rendering M2 does not yet support PipelineLayoutDesc.PushConstants; " +
                "reserved for a later milestone. Pass an empty PushConstants array.");

        // When DepthStencil is set, the DepthStencilFormat cap on the descriptor must agree. Two
        // fields represent the same concept at different layers (the nullable format summary vs
        // the structured state) so drift produces a confusing Dawn validation error downstream;
        // cross-check up front.
        if (desc.DepthStencil is { } ds && desc.DepthStencilFormat is { } dsf && ds.Format != dsf)
            throw new InvalidOperationException(
                $"PipelineDesc.DepthStencil.Format ({ds.Format}) does not match PipelineDesc.DepthStencilFormat ({dsf}).");

        var vertex = ResolveShader(desc.VertexShader);
        var fragment = ResolveShader(desc.FragmentShader);

        var vertexLayouts = new WgVertexBufferLayout[desc.VertexLayouts.Length];
        var attributePool = new WgVertexAttribute[desc.VertexLayouts.Span.SumAttributes()];
        var attrCursor = 0;

        var src = desc.VertexLayouts.Span;
        for (var i = 0; i < src.Length; i++)
        {
            var layout = src[i];
            var attrCount = layout.Attributes.Length;
            var attrStart = attrCursor;
            for (var a = 0; a < attrCount; a++)
            {
                var attr = layout.Attributes[a];
                attributePool[attrCursor++] = new WgVertexAttribute
                {
                    Format = FormatConversions.ToWgpu(attr.Format),
                    Offset = attr.Offset,
                    ShaderLocation = attr.ShaderLocation,
                };
            }
            vertexLayouts[i] = new WgVertexBufferLayout
            {
                ArrayStride = layout.Stride,
                StepMode = FormatConversions.ToWgpu(layout.StepMode),
                Attributes = new WebGpuSharp.WebGpuManagedSpan<WgVertexAttribute>(attributePool, attrStart, attrCount),
            };
        }

        var colorTargets = new WgColorTargetState[]
        {
            new WgColorTargetState
            {
                Format = FormatConversions.ToWgpu(desc.ColorFormat),
                Blend = null,
                WriteMask = WebGpuSharp.ColorWriteMask.All,
            },
        };

        // Resolve explicit pipeline layout from bind group layout handles when present. An empty
        // span means "use Dawn's auto layout" — appropriate when the shader has no resource
        // bindings (the M1 triangle path). A non-empty span implies the caller has an explicit
        // layout we build into a WgPipelineLayout; the native layout reference is held via the
        // descriptor capture for the duration of the Dawn call.
        //
        // Contract documented on `WebGpuRenderer.CreatePipeline(in ShaderProgramDesc, in PipelineDesc)`:
        // PipelineDesc.Layout (descriptor metadata) and PipelineDesc.BindGroupLayouts (runtime
        // handles, authoritative for the native build) must agree on group count. The Layout side
        // exists for cache-identity hashing and future push-constant validation. Pipeline build is
        // a one-shot cost (not per-frame) so this is a runtime check, not a Debug.Assert — drift
        // must surface in shipped builds too.
        if (desc.Layout is { } expectedLayout && desc.BindGroupLayouts.Length > 0
            && expectedLayout.Groups.Length != desc.BindGroupLayouts.Length)
        {
            throw new InvalidOperationException(
                $"PipelineDesc.Layout.Groups.Length ({expectedLayout.Groups.Length}) does not match " +
                $"PipelineDesc.BindGroupLayouts.Length ({desc.BindGroupLayouts.Length}). The native pipeline " +
                "is built from BindGroupLayouts; Layout is descriptor metadata. The two must agree.");
        }

        WgPipelineLayout? pipelineLayoutNative = null;
        var bglSpan = desc.BindGroupLayouts.Span;
        if (bglSpan.Length > 0)
        {
            var nativeLayouts = new WgBindGroupLayout[bglSpan.Length];
            for (var i = 0; i < bglSpan.Length; i++)
                nativeLayouts[i] = ResolveBindGroupLayout(bglSpan[i]);
            var layoutDesc = new WgPipelineLayoutDescriptor
            {
                Label = (desc.Name ?? string.Empty) + "_layout",
                BindGroupLayouts = nativeLayouts,
            };
            pipelineLayoutNative = Device.CreatePipelineLayout(in layoutDesc)
                ?? throw new InvalidOperationException("PipelineLayout creation returned null.");
        }

        var pipelineDesc = new WgRenderPipelineDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Layout = pipelineLayoutNative!,
            Vertex = new WgVertexState
            {
                Module = vertex,
                EntryPoint = string.IsNullOrEmpty(desc.VertexEntryPoint) ? "vs_main" : desc.VertexEntryPoint,
                Buffers = new WebGpuSharp.WebGpuManagedSpan<WgVertexBufferLayout>(vertexLayouts),
            },
            Primitive = new WgPrimitiveState
            {
                Topology = FormatConversions.ToWgpu(desc.Topology),
                StripIndexFormat = desc.Topology == PrimitiveTopology.LineStrip || desc.Topology == PrimitiveTopology.TriangleStrip
                    ? FormatConversions.ToWgpu(desc.StripIndexFormat)
                    : WebGpuSharp.IndexFormat.Undefined,
            },
            Multisample = new WgMultisampleState { Count = 1, Mask = uint.MaxValue },
            Fragment = new WgFragmentState
            {
                Module = fragment,
                EntryPoint = string.IsNullOrEmpty(desc.FragmentEntryPoint) ? "fs_main" : desc.FragmentEntryPoint,
                Targets = new WebGpuSharp.WebGpuManagedSpan<WgColorTargetState>(colorTargets),
            },
            DepthStencil = BuildDepthStencilState(desc),
        };

        return Device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException("RenderPipeline creation returned null.");
    }

    private static WgDepthStencilState? BuildDepthStencilState(in PipelineDesc desc)
    {
        if (desc.DepthStencil is not { } ds) return null;
        // M2 wires depth only; stencil face state defaults to "always pass, keep". StencilRead/
        // WriteMask are carried through so a later milestone can author stencil without changing
        // the descriptor shape.
        var neverCare = new WgStencilFaceState
        {
            Compare = WgCompareFunction.Always,
            FailOp = WgStencilOperation.Keep,
            DepthFailOp = WgStencilOperation.Keep,
            PassOp = WgStencilOperation.Keep,
        };
        return new WgDepthStencilState
        {
            Format = FormatConversions.ToWgpu(ds.Format),
            DepthWriteEnabled = ds.DepthWriteEnabled ? WgOptionalBool.True : WgOptionalBool.False,
            DepthCompare = FormatConversions.ToWgpu(ds.DepthCompare),
            StencilFront = neverCare,
            StencilBack = neverCare,
            StencilReadMask = ds.StencilReadMask,
            StencilWriteMask = ds.StencilWriteMask,
            DepthBias = 0,
            DepthBiasSlopeScale = 0f,
            DepthBiasClamp = 0f,
        };
    }

    /// <summary>Allocate a slot-table entry pointing at <paramref name="native"/> and return the
    /// resulting public handle. Each call mints a fresh handle even when the same native pipeline
    /// is registered repeatedly — that's the point of the split: shared cache below, distinct
    /// public identity above.</summary>
    public PipelineHandle RegisterPipeline(WgRenderPipeline native)
    {
        var (index, generation) = Pipelines.Add(native);
        return new PipelineHandle(index, generation);
    }

    public WgRenderPipeline ResolvePipeline(PipelineHandle h)
    {
        if (!Pipelines.TryGet(h.Index, h.Generation, out var pipeline))
            throw new StaleHandleException($"Pipeline handle ({h.Index},{h.Generation}) is stale or invalid.");
        return pipeline;
    }

    /// <summary>Synchronously invalidate the slot for <paramref name="h"/>. The underlying native
    /// <see cref="WgRenderPipeline"/> is owned by <see cref="PipelineCache"/> and outlives every
    /// public handle, so no native teardown happens here — only the public handle stops resolving.
    /// Mirrors <see cref="DetachBuffer"/>/<see cref="DetachShader"/> but without a native out-param
    /// because there is nothing for the caller to release.</summary>
    public bool DetachPipeline(PipelineHandle h) => Pipelines.Remove(h.Index, h.Generation);

    /// <summary>Slang-output shader creation. Dedupes the native <see cref="WgShaderModule"/> by
    /// <c>(WGSL source, entry point, stage)</c> BELOW the public handle layer — the GPU compile
    /// happens at most once per content key, but every call mints a fresh <see cref="ShaderHandle"/>.
    /// Matches the <see cref="PipelineCache"/> pattern: structural content dedupe under the hood,
    /// distinct public-handle identity above so two callers can independently destroy their
    /// handles without invalidating each other's still-live references to the shared native.</summary>
    public ShaderHandle CreateShaderModule(in ShaderModuleDesc moduleDesc)
    {
        var key = new ShaderModuleCacheKey(moduleDesc.Wgsl, moduleDesc.EntryPoint, moduleDesc.Stage);
        if (!_shaderModuleCache.TryGetValue(key, out var native))
        {
            var wgslDesc = new WgShaderModuleWGSLDescriptor { Code = moduleDesc.Wgsl };
            native = Device.CreateShaderModuleWGSL(moduleDesc.EntryPoint, in wgslDesc)
                ?? throw new InvalidOperationException("ShaderModule creation returned null.");
            _shaderModuleCache[key] = native;
        }
        var (index, generation) = Shaders.Add(native);
        return new ShaderHandle(index, generation);
    }

    // -------- Texture, view, sampler --------

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var td = new WgTextureDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Size = new WgExtent3D(desc.Width, desc.Height, desc.DepthOrArrayLayers == 0 ? 1 : desc.DepthOrArrayLayers),
            MipLevelCount = desc.MipLevelCount == 0 ? 1 : desc.MipLevelCount,
            SampleCount = desc.SampleCount == 0 ? 1 : desc.SampleCount,
            Dimension = FormatConversions.ToWgpu(desc.Dimension),
            Format = FormatConversions.ToWgpu(desc.Format),
            Usage = FormatConversions.ToWgpu(desc.Usage),
        };
        var texture = Device.CreateTexture(in td)
            ?? throw new InvalidOperationException("Texture creation returned null.");
        var (index, generation) = Textures.Add(texture);
        return new TextureHandle(index, generation);
    }

    public WgTexture ResolveTexture(TextureHandle h)
    {
        if (!Textures.TryGet(h.Index, h.Generation, out var native))
            throw new StaleHandleException($"Texture handle ({h.Index},{h.Generation}) is stale or invalid.");
        return native;
    }

    public bool DetachTexture(TextureHandle h, out WgTexture native) =>
        Textures.Detach(h.Index, h.Generation, out native);

    public RenderViewHandle CreateTextureView(TextureHandle texture, in RenderViewDesc desc)
    {
        var tex = ResolveTexture(texture);
        // Zero counts follow WebGPU's "remainder" convention: null MipLevelCount/ArrayLayerCount
        // in the descriptor tells Dawn to cover all remaining levels/layers beyond the base.
        var viewDesc = new WgTextureViewDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Format = FormatConversions.ToWgpu(desc.Format),
            Dimension = FormatConversions.ToWgpu(desc.Dimension),
            Aspect = FormatConversions.ToWgpu(desc.Aspect),
            BaseMipLevel = desc.BaseMipLevel,
            MipLevelCount = desc.MipLevelCount == 0 ? null : desc.MipLevelCount,
            BaseArrayLayer = desc.BaseArrayLayer,
            ArrayLayerCount = desc.ArrayLayerCount == 0 ? null : desc.ArrayLayerCount,
        };
        var view = tex.CreateView(in viewDesc)
            ?? throw new InvalidOperationException("TextureView creation returned null.");
        var (index, generation) = TextureViews.Add(view);
        return new RenderViewHandle(index, generation);
    }

    public WgTextureView ResolveTextureView(RenderViewHandle h)
    {
        if (!TextureViews.TryGet(h.Index, h.Generation, out var native))
            throw new StaleHandleException($"RenderView handle ({h.Index},{h.Generation}) is stale or invalid.");
        return native;
    }

    public bool DetachTextureView(RenderViewHandle h, out WgTextureView native) =>
        TextureViews.Detach(h.Index, h.Generation, out native);

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var sd = new WgSamplerDescriptor
        {
            Label = desc.Name ?? string.Empty,
            AddressModeU = FormatConversions.ToWgpu(desc.AddressU),
            AddressModeV = FormatConversions.ToWgpu(desc.AddressV),
            AddressModeW = FormatConversions.ToWgpu(desc.AddressW),
            MagFilter = FormatConversions.ToWgpuFilter(desc.MagFilter),
            MinFilter = FormatConversions.ToWgpuFilter(desc.MinFilter),
            MipmapFilter = FormatConversions.ToWgpuMipmapFilter(desc.MipmapFilter),
            LodMinClamp = 0f,
            LodMaxClamp = 32f,
            Compare = WgCompareFunction.Undefined,
            MaxAnisotropy = 1,
        };
        var sampler = Device.CreateSampler(ref sd)
            ?? throw new InvalidOperationException("Sampler creation returned null.");
        var (index, generation) = Samplers.Add(sampler);
        return new SamplerHandle(index, generation);
    }

    public WgSampler ResolveSampler(SamplerHandle h)
    {
        if (!Samplers.TryGet(h.Index, h.Generation, out var native))
            throw new StaleHandleException($"Sampler handle ({h.Index},{h.Generation}) is stale or invalid.");
        return native;
    }

    public bool DetachSampler(SamplerHandle h, out WgSampler native) =>
        Samplers.Detach(h.Index, h.Generation, out native);

    // -------- Bind group layout / bind group --------

    public BindGroupLayoutHandle CreateBindGroupLayout(WgBindGroupLayout native)
    {
        var (index, generation) = BindGroupLayouts.Add(native);
        return new BindGroupLayoutHandle(index, generation);
    }

    public WgBindGroupLayout ResolveBindGroupLayout(BindGroupLayoutHandle h)
    {
        if (!BindGroupLayouts.TryGet(h.Index, h.Generation, out var native))
            throw new StaleHandleException($"BindGroupLayout handle ({h.Index},{h.Generation}) is stale or invalid.");
        return native;
    }

    public bool DetachBindGroupLayout(BindGroupLayoutHandle h) =>
        BindGroupLayouts.Remove(h.Index, h.Generation);

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        var layout = ResolveBindGroupLayout(desc.Layout);

        var buffers = desc.Buffers.Span;
        var textures = desc.Textures.Span;
        var samplers = desc.Samplers.Span;
        var entries = new WgBindGroupEntry[buffers.Length + textures.Length + samplers.Length];
        var idx = 0;
        for (var i = 0; i < buffers.Length; i++)
        {
            var e = buffers[i];
            var buf = ResolveBuffer(e.Buffer);
            entries[idx++] = new WgBindGroupEntry
            {
                Binding = e.Binding,
                Buffer = buf,
                Offset = e.Offset,
                Size = e.Size == 0 ? null : e.Size,
            };
        }
        for (var i = 0; i < textures.Length; i++)
        {
            var e = textures[i];
            entries[idx++] = new WgBindGroupEntry
            {
                Binding = e.Binding,
                TextureView = ResolveTextureView(e.View),
            };
        }
        for (var i = 0; i < samplers.Length; i++)
        {
            var e = samplers[i];
            entries[idx++] = new WgBindGroupEntry
            {
                Binding = e.Binding,
                Sampler = ResolveSampler(e.Sampler),
            };
        }

        var bgDesc = new WgBindGroupDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Layout = layout,
            Entries = entries,
        };
        var bindGroup = Device.CreateBindGroup(in bgDesc)
            ?? throw new InvalidOperationException("BindGroup creation returned null.");
        var (index, generation) = BindGroups.Add(bindGroup);
        return new BindGroupHandle(index, generation);
    }

    public WgBindGroup ResolveBindGroup(BindGroupHandle h)
    {
        if (!BindGroups.TryGet(h.Index, h.Generation, out var native))
            throw new StaleHandleException($"BindGroup handle ({h.Index},{h.Generation}) is stale or invalid.");
        return native;
    }

    public bool DetachBindGroup(BindGroupHandle h, out WgBindGroup native) =>
        BindGroups.Detach(h.Index, h.Generation, out native);

    // -------- Texture upload --------

    /// <summary>Upload <paramref name="pixels"/> to <paramref name="texture"/> via
    /// <c>Queue.WriteTexture</c>. Assumes a tightly-packed 2D source matching mip level 0 layer 0
    /// (the M2 sample's path); broader mip/layer targeting is deferred to a later milestone.</summary>
    public void WriteTexture2D<T>(TextureHandle texture, uint width, uint height, uint bytesPerPixel, ReadOnlySpan<T> pixels)
        where T : unmanaged
    {
        var native = ResolveTexture(texture);
        var destination = new WgTexelCopyTextureInfo
        {
            Texture = native,
            MipLevel = 0,
            Origin = new WgOrigin3D { X = 0, Y = 0, Z = 0 },
            Aspect = WebGpuSharp.TextureAspect.All,
        };
        var layout = new WgTexelCopyBufferLayout
        {
            Offset = 0,
            BytesPerRow = width * bytesPerPixel,
            RowsPerImage = height,
        };
        var writeSize = new WgExtent3D(width, height, 1);
        Queue.WriteTexture(in destination, pixels, in layout, in writeSize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BindGroups.Clear();
        BindGroupLayouts.Clear();
        Samplers.Clear();
        TextureViews.Clear();
        Textures.Clear();
        Pipelines.Clear();
        Buffers.Clear();
        Shaders.Clear();
        _shaderModuleCache.Clear();
        // WebGPUSharp's safe wrappers release native handles via finalizers; explicit teardown
        // ordering here is mostly documentation. The instance must outlive surfaces and devices,
        // so the owning Renderer disposes those first.
    }
}

internal static class VertexLayoutExtensions
{
    public static int SumAttributes(this ReadOnlySpan<VertexBufferLayoutDesc> layouts)
    {
        var n = 0;
        for (var i = 0; i < layouts.Length; i++) n += layouts[i].Attributes.Length;
        return n;
    }
}

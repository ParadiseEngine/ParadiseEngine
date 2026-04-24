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
        // Reject depth/stencil settings explicitly in M1 instead of accepting them and silently
        // discarding — the descriptor field exists for M2 but the backend has no plumbing for it
        // yet. NotSupportedException makes the gap visible at the call site instead of producing
        // a pipeline that renders as if depth were never configured.
        if (desc.DepthStencilFormat is not null)
            throw new NotSupportedException(
                "PipelineDesc.DepthStencilFormat is reserved for M2 (#43). The M1 backend does not " +
                "configure depth-stencil state on the WebGPU pipeline; pass null until depth lands.");

        // PipelineDesc.Layout is carried in the public descriptor and hashed into cache identity
        // so the M2 API shape stays stable (no migration when bind groups land). In M1 the backend
        // always requests Dawn's "auto" layout from the shader module, so a non-empty Layout would
        // be silently dropped — reject explicitly. Empty Layout records (Groups=[], PushConstants=[])
        // are accepted because ShaderProgramLoader.BuildProgramDesc constructs one for every Slang
        // program; M1 just has no bindings yet. M2 (#43) will replace this guard with a real
        // WebGPU PipelineLayout build.
        if (desc.Layout is { } l && (l.Groups.Length > 0 || l.PushConstants.Length > 0))
            throw new NotSupportedException(
                "PipelineDesc.Layout with non-empty bind groups or push constants is reserved for M2 (#43). " +
                "The M1 backend uses Dawn's auto layout from the shader module; explicit layouts are not " +
                "plumbed through yet. Pass a layout with empty Groups and PushConstants (or null) until M2.");

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

        var pipelineDesc = new WgRenderPipelineDescriptor
        {
            Label = desc.Name ?? string.Empty,
            // M1 binds no resources beyond a vertex buffer; passing a null layout requests Dawn's
            // "auto" layout from the shader module — appropriate for the empty-binding case. M2
            // populates an explicit PipelineLayout when bind groups land.
            Layout = null!,
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
        };

        return Device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException("RenderPipeline creation returned null.");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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

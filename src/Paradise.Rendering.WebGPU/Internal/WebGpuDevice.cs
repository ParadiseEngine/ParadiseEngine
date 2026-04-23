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

    // Content-keyed shader-module dedupe. Identical (WGSL source + entry point + stage) tuples
    // resolve to the same ShaderHandle so that Slang programs loaded twice produce a single
    // backend module per stage — keeps the pipeline cache key stable and prevents shader-slot
    // accumulation in the long-running CreatePipeline(ShaderProgramDesc, ...) path.
    private readonly System.Collections.Generic.Dictionary<ShaderModuleCacheKey, ShaderHandle> _shaderModuleCache = new();

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

    public bool DestroyShader(ShaderHandle h)
    {
        // Drop any cache entry that maps to this handle so a future CreateShaderModule with the
        // same WGSL/entry-point/stage tuple compiles a fresh module instead of returning a stale one.
        ShaderModuleCacheKey? toRemove = null;
        foreach (var kvp in _shaderModuleCache)
        {
            if (kvp.Value.Equals(h)) { toRemove = kvp.Key; break; }
        }
        if (toRemove is { } key) _shaderModuleCache.Remove(key);
        return Shaders.Remove(h.Index, h.Generation);
    }

    public bool TryResolveShader(ShaderHandle h, out WgShaderModule module) =>
        Shaders.TryGet(h.Index, h.Generation, out module);

    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        var bd = new WgBufferDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Size = desc.Size,
            Usage = FormatConversions.ToWgpu(desc.Usage),
            MappedAtCreation = desc.MappedAtCreation,
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

    public bool DestroyBuffer(BufferHandle h)
    {
        if (Buffers.TryGet(h.Index, h.Generation, out var buffer))
        {
            buffer.Destroy();
        }
        return Buffers.Remove(h.Index, h.Generation);
    }

    public PipelineHandle CreatePipeline(in PipelineDesc desc)
    {
        // Reject depth/stencil settings explicitly in M1 instead of accepting them and silently
        // discarding — the descriptor field exists for M2 but the backend has no plumbing for it
        // yet. NotSupportedException makes the gap visible at the call site instead of producing
        // a pipeline that renders as if depth were never configured.
        if (desc.DepthStencilFormat is not null)
            throw new NotSupportedException(
                "PipelineDesc.DepthStencilFormat is reserved for M2 (#43). The M1 backend does not " +
                "configure depth-stencil state on the WebGPU pipeline; pass null until depth lands.");

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

        var pipeline = Device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException("RenderPipeline creation returned null.");

        var (index, generation) = Pipelines.Add(pipeline);
        return new PipelineHandle(index, generation);
    }

    public WgRenderPipeline ResolvePipeline(PipelineHandle h)
    {
        if (!Pipelines.TryGet(h.Index, h.Generation, out var pipeline))
            throw new StaleHandleException($"Pipeline handle ({h.Index},{h.Generation}) is stale or invalid.");
        return pipeline;
    }

    public bool DestroyPipeline(PipelineHandle h) => Pipelines.Remove(h.Index, h.Generation);

    public ShaderHandle CreateShaderModule(in ShaderModuleDesc moduleDesc)
    {
        // Dedupe by (WGSL source, entry point, stage). Two ShaderProgramDesc loads of the same
        // shader return the same ShaderHandle here, which keeps PipelineDesc.ContentHash stable
        // across CreatePipeline(ShaderProgramDesc, ...) calls and lets the pipeline cache hit on
        // logical shader identity rather than on per-call slot indices.
        var key = new ShaderModuleCacheKey(moduleDesc.Wgsl, moduleDesc.EntryPoint, moduleDesc.Stage);
        if (_shaderModuleCache.TryGetValue(key, out var cached)) return cached;
        var handle = CreateShader(moduleDesc.Wgsl, moduleDesc.EntryPoint);
        _shaderModuleCache[key] = handle;
        return handle;
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

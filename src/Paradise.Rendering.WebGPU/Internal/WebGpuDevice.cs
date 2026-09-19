using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Rendering.Internal;

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
using WgTextureView = WebGpuSharp.TextureView;
using WgTextureViewDescriptor = WebGpuSharp.TextureViewDescriptor;
using WgTextureDescriptor = WebGpuSharp.TextureDescriptor;
using WgExtent3D = WebGpuSharp.Extent3D;
using WgSampler = WebGpuSharp.Sampler;
using WgSamplerDescriptor = WebGpuSharp.SamplerDescriptor;
using WgCompareFunction = WebGpuSharp.CompareFunction;
using WgBindGroup = WebGpuSharp.BindGroup;
using WgBindGroupDescriptor = WebGpuSharp.BindGroupDescriptor;
using WgBindGroupEntry = WebGpuSharp.BindGroupEntry;
using WgBindGroupLayout = WebGpuSharp.BindGroupLayout;
using WgBindGroupLayoutDescriptor = WebGpuSharp.BindGroupLayoutDescriptor;
using WgBindGroupLayoutEntry = WebGpuSharp.BindGroupLayoutEntry;
using WgBufferBindingLayout = WebGpuSharp.BufferBindingLayout;
using WgBufferBindingType = WebGpuSharp.BufferBindingType;
using WgTextureBindingLayout = WebGpuSharp.TextureBindingLayout;
using WgTextureSampleType = WebGpuSharp.TextureSampleType;
using WgTextureViewDimension = WebGpuSharp.TextureViewDimension;
using WgSamplerBindingLayout = WebGpuSharp.SamplerBindingLayout;
using WgSamplerBindingType = WebGpuSharp.SamplerBindingType;
using WgPipelineLayout = WebGpuSharp.PipelineLayout;
using WgPipelineLayoutDescriptor = WebGpuSharp.PipelineLayoutDescriptor;
using WgComputePipeline = WebGpuSharp.ComputePipeline;
using WgComputePipelineDescriptor = WebGpuSharp.ComputePipelineDescriptor;
using WgComputeState = WebGpuSharp.ComputeState;
using WgStorageTextureBindingLayout = WebGpuSharp.StorageTextureBindingLayout;
using WgStorageTextureAccess = WebGpuSharp.StorageTextureAccess;
using WgDepthStencilState = WebGpuSharp.DepthStencilState;
using WgOptionalBool = WebGpuSharp.OptionalBool;
using WgBlendState = WebGpuSharp.BlendState;
using WgBlendComponent = WebGpuSharp.BlendComponent;
using WgBlendOperation = WebGpuSharp.BlendOperation;
using WgBlendFactor = WebGpuSharp.BlendFactor;
using WgFeatureName = WebGpuSharp.FeatureName;
using WgShaderStage = WebGpuSharp.ShaderStage;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>Owns the long-lived WebGPU instance/adapter/device/queue chain plus the slot tables
/// that map <see cref="Paradise.Rendering"/> handles to native WebGPUSharp objects. Constructed
/// once per <see cref="WebGpuRenderer"/>. Adapter selection takes an optional compatible
/// <see cref="WgSurface"/> — pass <c>null</c> to drive the headless adapter path (no swapchain).</summary>
internal sealed partial class WebGpuDevice : IDisposable
{
    public WgInstance Instance { get; }
    public WgAdapter Adapter { get; }
    public WgDevice Device { get; }
    public WgQueue Queue { get; }

    public SlotTable<WgShaderModule> Shaders { get; } = new();
    public SlotTable<WgBuffer> Buffers { get; } = new();
    public SlotTable<WgRenderPipeline> Pipelines { get; } = new();
    // Compute pipelines live in their own slot space (and their own public handle type) so the
    // render-pipeline side tables — depth flags, content cache — never see them.
    public SlotTable<WgComputePipeline> ComputePipelines { get; } = new();
    // One default view per texture covers the common sampled-2D + depth-attachment cases; explicit
    // TextureViews (below) provide per-layer / D2-array views for the shadow-map array.
    public SlotTable<TextureEntry> Textures { get; } = new();
    public SlotTable<WgTextureView> TextureViews { get; } = new();
    public SlotTable<WgSampler> Samplers { get; } = new();
    public SlotTable<WgBindGroup> BindGroups { get; } = new();

    /// <summary>True when the adapter granted TextureCompressionBC — BC-format texture creation
    /// requires it (the transcoder falls back to RGBA32 otherwise).</summary>
    public bool SupportsBc { get; private set; }

    /// <summary>True when the adapter granted timestamp queries — what per-pass GPU timing needs.</summary>
    public bool SupportsTimestampQuery { get; private set; }

    /// <summary>Device-required alignment for dynamic uniform-buffer offsets. Draw-UBO rings
    /// must stride by a multiple of this (WebGPU guarantees ≤ 256; we clamp up to 256 so ring
    /// layouts stay stable across adapters).</summary>
    public uint UniformBufferOffsetAlignment { get; private set; } = 256;

    private readonly RefCountedCache<string, WgBindGroupLayout> _bindGroupLayoutCache = new();
    private readonly Dictionary<BindGroupHandle, RefCountedCache<string, WgBindGroupLayout>.Lease> _bindGroupLayouts = [];
    private readonly RefCountedCache<ShaderModuleCacheKey, WgShaderModule> _shaderModuleCache = new();
    private readonly Dictionary<ShaderHandle, RefCountedCache<ShaderModuleCacheKey, WgShaderModule>.Lease> _shaderModuleLeases = [];
    private readonly Dictionary<ComputePipelineHandle, NativeResource<WgComputePipeline>> _computePipelineResources = [];

    private readonly record struct ShaderModuleCacheKey(string Wgsl, string EntryPoint, ShaderStage Stage);

    internal int ShaderCacheCount => _shaderModuleCache.Count;
    internal int BindGroupLayoutCacheCount => _bindGroupLayoutCache.Count;

    private bool _disposed;

    private WebGpuDevice(WgInstance instance, WgAdapter adapter, WgDevice device, WgQueue queue)
    {
        Instance = instance;
        Adapter = adapter;
        Device = device;
        Queue = queue;
    }

    public static WebGpuDevice Create(WgInstance instance, WgSurface? compatibleSurface, ILogger? logger = null)
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

        // Negotiate optional features up-front: BC texture compression is required for the
        // KTX2→BC transcode path; when absent the asset layer falls back to RGBA32 uploads.
        var supportsBc = adapter.HasFeature(WgFeatureName.TextureCompressionBC);
        // Timestamp queries cost nothing until a pass asks to be timed; requesting them up front
        // is what lets a profiler be switched on at runtime rather than at device creation. Only a
        // profiling build asks: a shipping build must not depend on an optional feature it never uses.
#if PARADISE_PROFILING
        var supportsTimestamps = adapter.HasFeature(WgFeatureName.TimestampQuery);
#else
        var supportsTimestamps = false;
#endif

        // NOT `static` lambdas any more: they capture the logger, which costs one closure per
        // device — once, at creation — and is what lets a host route Dawn's validation errors
        // anywhere but stderr. Dawn calls both of these on threads the engine did not create, so
        // whatever sink is behind this logger has to be thread-safe.
        var log = logger ?? NullLogger.Instance;
        var deviceDesc = new WgDeviceDescriptor
        {
            Label = "Paradise.Rendering.WebGPU",
            UncapturedErrorCallback = (type, message) =>
            {
                if (!log.IsEnabled(LogLevel.Error)) return;
                var text = message.Length == 0 ? "(no message)" : System.Text.Encoding.UTF8.GetString(message);
                LogUncapturedError(log, type, text);
            },
            DeviceLostCallback = (reason, message) =>
            {
                if (reason == WebGpuSharp.DeviceLostReason.Destroyed) return;
                if (!log.IsEnabled(LogLevel.Critical)) return;
                var text = message.Length == 0 ? "(no message)" : System.Text.Encoding.UTF8.GetString(message);
                LogDeviceLost(log, reason, text);
            },
        };
        var features = new System.Collections.Generic.List<WgFeatureName>(2);
        if (supportsBc) features.Add(WgFeatureName.TextureCompressionBC);
        if (supportsTimestamps) features.Add(WgFeatureName.TimestampQuery);
        if (features.Count > 0) deviceDesc.RequiredFeatures = features.ToArray();

        var device = adapter.RequestDeviceSync(in deviceDesc, AdapterTimeoutNs)
            ?? throw new InvalidOperationException("WebGPU device creation failed.");

        var queue = device.GetQueue();
        var result = new WebGpuDevice(instance, adapter, device, queue) { SupportsBc = supportsBc, SupportsTimestampQuery = supportsTimestamps };
        var limits = device.GetLimits();
        result.UniformBufferOffsetAlignment = Math.Max(256, limits.MinUniformBufferOffsetAlignment);
        return result;
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

    /// <summary>Invalidates a shader slot and releases its share of the native module cache.</summary>
    public bool DetachShader(ShaderHandle h, out WgShaderModule native)
    {
        if (!Shaders.Detach(h.Index, h.Generation, out native)) return false;
        if (_shaderModuleLeases.Remove(h, out var lease)) lease.Dispose();
        return true;
    }

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

    /// <summary>Synchronously invalidate the slot and transfer the native buffer to the caller.</summary>
    /// <remarks>The caller invokes native Destroy; WebGPU retains storage used by submitted work.
    /// After detachment, ResolveBuffer throws StaleHandleException for the old handle.</remarks>
    public bool DetachBuffer(BufferHandle h, out WgBuffer native) =>
        Buffers.Detach(h.Index, h.Generation, out native);

    // -------- Textures / samplers / bind groups (M2) --------

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        var td = new WgTextureDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Size = new WgExtent3D(desc.Width, desc.Height, Math.Max(1, desc.DepthOrArrayLayers)),
            MipLevelCount = Math.Max(1, desc.MipLevelCount),
            SampleCount = Math.Max(1, desc.SampleCount),
            Dimension = FormatConversions.ToWgpu(desc.Dimension),
            Format = FormatConversions.ToWgpu(desc.Format),
            Usage = FormatConversions.ToWgpu(desc.Usage),
        };
        var texture = Device.CreateTexture(in td)
            ?? throw new InvalidOperationException("Texture creation returned null.");
        var view = texture.CreateView()
            ?? throw new InvalidOperationException("Texture view creation returned null.");
        var (index, generation) = Textures.Add(new TextureEntry(texture, view));
        return new TextureHandle(index, generation);
    }

    public TextureEntry ResolveTexture(TextureHandle h)
    {
        if (!Textures.TryGet(h.Index, h.Generation, out var entry))
            throw new StaleHandleException($"Texture handle ({h.Index},{h.Generation}) is stale or invalid.");
        return entry;
    }

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        var texture = ResolveTexture(desc.Texture).Texture;
        var vd = new WgTextureViewDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Dimension = FormatConversions.ToWgpu(desc.Dimension),
            BaseArrayLayer = desc.BaseArrayLayer,
            ArrayLayerCount = Math.Max(1, desc.ArrayLayerCount),
            BaseMipLevel = desc.BaseMipLevel,
            MipLevelCount = Math.Max(1, desc.MipLevelCount),
        };
        var view = texture.CreateView(in vd)
            ?? throw new InvalidOperationException("Explicit texture view creation returned null.");
        var (index, generation) = TextureViews.Add(view);
        return new TextureViewHandle(index, generation);
    }

    public WgTextureView ResolveTextureView(TextureViewHandle h)
    {
        if (!TextureViews.TryGet(h.Index, h.Generation, out var view))
            throw new StaleHandleException($"Texture view handle ({h.Index},{h.Generation}) is stale or invalid.");
        return view;
    }

    public bool DetachTextureView(TextureViewHandle h, out WgTextureView native) =>
        TextureViews.Detach(h.Index, h.Generation, out native);

    public bool DetachTexture(TextureHandle h, out TextureEntry native) =>
        Textures.Detach(h.Index, h.Generation, out native);

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var sd = new WgSamplerDescriptor
        {
            Label = desc.Name ?? string.Empty,
            AddressModeU = FormatConversions.ToWgpu(desc.AddressU),
            AddressModeV = FormatConversions.ToWgpu(desc.AddressV),
            AddressModeW = FormatConversions.ToWgpu(desc.AddressW),
            MagFilter = FormatConversions.ToWgpu(desc.MagFilter),
            MinFilter = FormatConversions.ToWgpu(desc.MinFilter),
            MipmapFilter = FormatConversions.ToWgpuMipmap(desc.MipmapFilter),
            MaxAnisotropy = Math.Max((ushort)1, desc.MaxAnisotropy),
            // A non-Undefined compare turns this into a comparison sampler (sampler_comparison),
            // consumed by shadow-map textureSampleCompareLevel.
            Compare = desc.Compare is { } compare ? FormatConversions.ToWgpu(compare) : WgCompareFunction.Undefined,
        };
        var sampler = Device.CreateSampler(ref sd)
            ?? throw new InvalidOperationException("Sampler creation returned null.");
        var (index, generation) = Samplers.Add(sampler);
        return new SamplerHandle(index, generation);
    }

    public WgSampler ResolveSampler(SamplerHandle h)
    {
        if (!Samplers.TryGet(h.Index, h.Generation, out var sampler))
            throw new StaleHandleException($"Sampler handle ({h.Index},{h.Generation}) is stale or invalid.");
        return sampler;
    }

    public bool DetachSampler(SamplerHandle h, out WgSampler native) =>
        Samplers.Detach(h.Index, h.Generation, out native);

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        var src = desc.Entries.Span;
        var entries = new WgBindGroupEntry[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            ref readonly var e = ref src[i];
            entries[i] = e.Kind switch
            {
                BindGroupEntryKind.Buffer => new WgBindGroupEntry
                {
                    Binding = e.Binding,
                    Buffer = ResolveBuffer(e.Buffer),
                    Offset = e.Offset,
                    Size = e.Size,
                },
                BindGroupEntryKind.Texture => new WgBindGroupEntry
                {
                    Binding = e.Binding,
                    TextureView = ResolveTexture(e.Texture).View,
                },
                BindGroupEntryKind.Sampler => new WgBindGroupEntry
                {
                    Binding = e.Binding,
                    Sampler = ResolveSampler(e.Sampler),
                },
                BindGroupEntryKind.TextureView => new WgBindGroupEntry
                {
                    Binding = e.Binding,
                    TextureView = ResolveTextureView(e.View),
                },
                _ => throw new NotSupportedException($"Bind group entry kind '{e.Kind}' is not supported."),
            };
        }

        var layout = AcquireBindGroupLayout(desc.Layout);
        try
        {
            var bd = new WgBindGroupDescriptor
            {
                Label = desc.Name ?? string.Empty,
                Layout = layout.Value,
                Entries = entries,
            };
            var bindGroup = Device.CreateBindGroup(bd)
                ?? throw new InvalidOperationException("BindGroup creation returned null.");
            var (index, generation) = BindGroups.Add(bindGroup);
            var handle = new BindGroupHandle(index, generation);
            _bindGroupLayouts.Add(handle, layout);
            return handle;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    public WgBindGroup ResolveBindGroup(BindGroupHandle h)
    {
        if (!BindGroups.TryGet(h.Index, h.Generation, out var group))
            throw new StaleHandleException($"BindGroup handle ({h.Index},{h.Generation}) is stale or invalid.");
        return group;
    }

    public bool DetachBindGroup(BindGroupHandle h, out WgBindGroup native)
    {
        if (!BindGroups.Detach(h.Index, h.Generation, out native)) return false;
        if (_bindGroupLayouts.Remove(h, out var layout)) layout.Dispose();
        return true;
    }

    /// <summary>Content-keyed native bind-group-layout lookup. Pipelines and bind groups built
    /// from structurally identical <see cref="BindGroupLayoutDesc"/>s share one native layout —
    /// Dawn's compatibility rule made trivially true by construction.</summary>
    private RefCountedCache<string, WgBindGroupLayout>.Lease AcquireBindGroupLayout(BindGroupLayoutDesc desc) =>
        _bindGroupLayoutCache.Acquire(BindGroupLayoutKey(desc), () => BuildBindGroupLayout(desc));

    private WgBindGroupLayout BuildBindGroupLayout(BindGroupLayoutDesc desc)
    {
        var entries = new WgBindGroupLayoutEntry[desc.Entries.Length];
        for (var i = 0; i < desc.Entries.Length; i++)
        {
            var e = desc.Entries[i];
            var entry = new WgBindGroupLayoutEntry
            {
                Binding = e.Binding,
                Visibility = ToWgpuVisibility(e.Visibility),
            };
            switch (e.Type)
            {
                case BindingResourceType.UniformBuffer:
                    entry.Buffer = new WgBufferBindingLayout
                    {
                        Type = WgBufferBindingType.Uniform,
                        HasDynamicOffset = e.HasDynamicOffset,
                        MinBindingSize = e.MinBufferSize,
                    };
                    break;
                case BindingResourceType.StorageBuffer:
                    entry.Buffer = new WgBufferBindingLayout
                    {
                        Type = WgBufferBindingType.Storage,
                        HasDynamicOffset = e.HasDynamicOffset,
                        MinBindingSize = e.MinBufferSize,
                    };
                    break;
                case BindingResourceType.ReadonlyStorageBuffer:
                    entry.Buffer = new WgBufferBindingLayout
                    {
                        Type = WgBufferBindingType.ReadOnlyStorage,
                        HasDynamicOffset = e.HasDynamicOffset,
                        MinBindingSize = e.MinBufferSize,
                    };
                    break;
                case BindingResourceType.SampledTexture:
                case BindingResourceType.SampledTextureArray:
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.Float,
                        ViewDimension = e.Type == BindingResourceType.SampledTextureArray ? WgTextureViewDimension.D2Array : WgTextureViewDimension.D2,
                        Multisampled = false,
                    };
                    break;
                case BindingResourceType.UnfilterableFloatTexture:
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.UnfilterableFloat,
                        ViewDimension = WgTextureViewDimension.D2,
                        Multisampled = false,
                    };
                    break;
                case BindingResourceType.DepthTexture:
                    // A depth texture read as texture_depth_2d (shadow maps). SampleType must be
                    // Depth to pair with a comparison sampler.
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.Depth,
                        ViewDimension = WgTextureViewDimension.D2,
                        Multisampled = false,
                    };
                    break;
                case BindingResourceType.DepthTextureArray:
                    // A depth texture array read as texture_depth_2d_array (per-light shadow-map
                    // array). Depth sample type + D2Array view; paired with a comparison sampler.
                    entry.Texture = new WgTextureBindingLayout
                    {
                        SampleType = WgTextureSampleType.Depth,
                        ViewDimension = WgTextureViewDimension.D2Array,
                        Multisampled = false,
                    };
                    break;
                case BindingResourceType.StorageTexture:
                    // WebGPU requires format + access in the LAYOUT for storage textures; the
                    // loader guarantees a concrete format (it throws when [format] is missing).
                    if (e.StorageFormat == TextureFormat.Undefined)
                        throw new InvalidOperationException(
                            $"StorageTexture binding {e.Binding} has no StorageFormat — the layout cannot be built.");
                    entry.StorageTexture = new WgStorageTextureBindingLayout
                    {
                        Access = ToWgpuAccess(e.Access),
                        Format = FormatConversions.ToWgpu(e.StorageFormat),
                        ViewDimension = WgTextureViewDimension.D2,
                    };
                    break;
                case BindingResourceType.Sampler:
                    entry.Sampler = new WgSamplerBindingLayout { Type = WgSamplerBindingType.Filtering };
                    break;
                case BindingResourceType.ComparisonSampler:
                    entry.Sampler = new WgSamplerBindingLayout { Type = WgSamplerBindingType.Comparison };
                    break;
                default:
                    throw new NotSupportedException($"Binding resource type '{e.Type}' is not supported yet.");
            }
            entries[i] = entry;
        }

        var native = Device.CreateBindGroupLayout(new WgBindGroupLayoutDescriptor { Entries = entries })
            ?? throw new InvalidOperationException("BindGroupLayout creation returned null.");
        return native;
    }

    private static WgShaderStage ToWgpuVisibility(ShaderStage stage)
    {
        var w = WgShaderStage.None;
        if ((stage & ShaderStage.Vertex) != 0) w |= WgShaderStage.Vertex;
        if ((stage & ShaderStage.Fragment) != 0) w |= WgShaderStage.Fragment;
        if ((stage & ShaderStage.Compute) != 0) w |= WgShaderStage.Compute;
        return w;
    }

    // Canonical string key over the layout's structural content. Allocation happens only on
    // layout creation/lookup (a handful per renderer lifetime), not per frame.
    private static string BindGroupLayoutKey(BindGroupLayoutDesc desc)
    {
        var sb = new System.Text.StringBuilder(desc.Entries.Length * 16);
        foreach (var e in desc.Entries)
        {
            sb.Append(e.Binding).Append(':')
              .Append((int)e.Visibility).Append(':')
              .Append((int)e.Type).Append(':')
              .Append(e.MinBufferSize).Append(':')
              .Append(e.HasDynamicOffset ? '1' : '0').Append(':')
              .Append((int)e.StorageFormat).Append(':')
              .Append((int)e.Access).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>Build a native pipeline layout from the desc's groups. WebGPU requires dense
    /// group indices, so gaps are filled with empty bind-group layouts.</summary>
    private NativeResource<WgPipelineLayout> BuildPipelineLayout(PipelineLayoutDesc layout)
    {
        if (layout.PushConstants.Length > 0)
            throw new NotSupportedException("Push constants are not supported by the WebGPU backend.");

        uint maxGroup = 0;
        foreach (var g in layout.Groups) maxGroup = Math.Max(maxGroup, g.GroupIndex);

        var layouts = new WgBindGroupLayout[maxGroup + 1];
        var dependencies = new List<IDisposable>(layouts.Length);
        try
        {
            for (var i = 0; i < layouts.Length; i++)
            {
                BindGroupLayoutDesc? match = null;
                foreach (var g in layout.Groups)
                {
                    if (g.GroupIndex == i) { match = g; break; }
                }
                var lease = AcquireBindGroupLayout(match ?? new BindGroupLayoutDesc((uint)i, []));
                dependencies.Add(lease);
                layouts[i] = lease.Value;
            }
            var native = Device.CreatePipelineLayout(new WgPipelineLayoutDescriptor { BindGroupLayouts = layouts })
                ?? throw new InvalidOperationException("PipelineLayout creation returned null.");
            return new NativeResource<WgPipelineLayout>(native, dependencies);
        }
        catch
        {
            foreach (var dependency in dependencies) dependency.Dispose();
            throw;
        }
    }

    /// <summary>Build a native WebGPU pipeline from <paramref name="desc"/> without allocating a
    /// slot-table entry. Used by <c>WebGpuRenderer.CreatePipeline</c> in conjunction with
    /// the cache + <see cref="RegisterPipeline"/>: the cache stores the native pipeline once per
    /// content hash, every CreatePipeline call mints its own public handle pointing at the
    /// shared native pipeline.</summary>
    public NativeResource<WgRenderPipeline> BuildNativePipeline(in PipelineDesc desc)
    {
        var dependencies = new List<IDisposable>();
        try
        {
            RetainShader(desc.VertexShader, dependencies);
            if (desc.FragmentShader.IsValid) RetainShader(desc.FragmentShader, dependencies);
            NativeResource<WgPipelineLayout>? layout = null;
            if (desc.Layout is { } explicitLayout && (explicitLayout.Groups.Length > 0 || explicitLayout.PushConstants.Length > 0))
            {
                layout = BuildPipelineLayout(explicitLayout);
                dependencies.Add(layout);
            }
            return new NativeResource<WgRenderPipeline>(BuildNativePipelineCore(desc, layout?.Native), dependencies);
        }
        catch
        {
            foreach (var dependency in dependencies) dependency.Dispose();
            throw;
        }
    }

    private void RetainShader(ShaderHandle handle, List<IDisposable> dependencies)
    {
        ResolveShader(handle);
        if (_shaderModuleLeases.TryGetValue(handle, out var lease)) dependencies.Add(lease.Retain());
    }

    private WgRenderPipeline BuildNativePipelineCore(in PipelineDesc desc, WgPipelineLayout? pipelineLayout)
    {
        var vertex = ResolveShader(desc.VertexShader);
        // Depth-only pipelines (e.g. the shadow caster) carry no fragment shader → no color target.
        var hasFragment = desc.FragmentShader.IsValid;
        var fragment = hasFragment ? ResolveShader(desc.FragmentShader) : default;

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
                // Standard alpha compositing for AlphaBlend, additive accumulation for Additive,
                // null (opaque) otherwise.
                Blend = desc.Blend switch
                {
                    BlendMode.AlphaBlend => new WgBlendState
                    {
                        Color = new WgBlendComponent
                        {
                            Operation = WgBlendOperation.Add,
                            SrcFactor = WgBlendFactor.SrcAlpha,
                            DstFactor = WgBlendFactor.OneMinusSrcAlpha,
                        },
                        Alpha = new WgBlendComponent
                        {
                            Operation = WgBlendOperation.Add,
                            SrcFactor = WgBlendFactor.One,
                            DstFactor = WgBlendFactor.OneMinusSrcAlpha,
                        },
                    },
                    BlendMode.Additive => new WgBlendState
                    {
                        Color = new WgBlendComponent
                        {
                            Operation = WgBlendOperation.Add,
                            SrcFactor = WgBlendFactor.One,
                            DstFactor = WgBlendFactor.One,
                        },
                        Alpha = new WgBlendComponent
                        {
                            Operation = WgBlendOperation.Add,
                            SrcFactor = WgBlendFactor.One,
                            DstFactor = WgBlendFactor.One,
                        },
                    },
                    _ => null,
                },
                WriteMask = WebGpuSharp.ColorWriteMask.All,
            },
        };

        var pipelineDesc = new WgRenderPipelineDescriptor
        {
            Label = desc.Name ?? string.Empty,
            Layout = pipelineLayout!,
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
        };
        if (hasFragment)
        {
            pipelineDesc.Fragment = new WgFragmentState
            {
                Module = fragment!, // non-null when hasFragment
                EntryPoint = string.IsNullOrEmpty(desc.FragmentEntryPoint) ? "fs_main" : desc.FragmentEntryPoint,
                Targets = new WebGpuSharp.WebGpuManagedSpan<WgColorTargetState>(colorTargets),
            };
        }
        // else: depth-only pipeline (no fragment stage, no color targets) — valid in WebGPU when a
        // depth-stencil state is present (set below).

        if (desc.DepthStencilFormat is { } depthFormat)
        {
            pipelineDesc.DepthStencil = new WgDepthStencilState
            {
                Format = FormatConversions.ToWgpu(depthFormat),
                DepthWriteEnabled = desc.DepthWriteEnabled ? WgOptionalBool.True : WgOptionalBool.False,
                DepthCompare = FormatConversions.ToWgpu(desc.DepthCompare),
            };
        }

        return Device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException("RenderPipeline creation returned null.");
    }

    /// <summary>The backend's spelling of a storage-texture access mode. (The comment that used
    /// to sit here described minting a pipeline handle — it had been orphaned from another method
    /// and documented this one incorrectly.)</summary>
    private static WgStorageTextureAccess ToWgpuAccess(StorageTextureAccess access) => access switch
    {
        StorageTextureAccess.WriteOnly => WgStorageTextureAccess.WriteOnly,
        StorageTextureAccess.ReadOnly => WgStorageTextureAccess.ReadOnly,
        StorageTextureAccess.ReadWrite => WgStorageTextureAccess.ReadWrite,
        _ => throw new NotSupportedException($"Storage texture access '{access}' has no WebGPU mapping."),
    };

    /// <summary>Build a native compute pipeline. Layout follows the render path's rule: explicit
    /// when the program reflects groups, otherwise Dawn's implicit/auto layout.</summary>
    public NativeResource<WgComputePipeline> BuildNativeComputePipeline(ShaderHandle shader, string entryPoint, PipelineLayoutDesc? layout)
    {
        var dependencies = new List<IDisposable>();
        try
        {
            RetainShader(shader, dependencies);
            NativeResource<WgPipelineLayout>? pipelineLayout = null;
            if (layout is not null && (layout.Groups.Length > 0 || layout.PushConstants.Length > 0))
            {
                pipelineLayout = BuildPipelineLayout(layout);
                dependencies.Add(pipelineLayout);
            }
            var desc = new WgComputePipelineDescriptor
            {
                Layout = pipelineLayout?.Native!,
                Compute = new WgComputeState { Module = ResolveShader(shader), EntryPoint = entryPoint },
            };
            var native = Device.CreateComputePipelineSync(in desc)
                ?? throw new InvalidOperationException("ComputePipeline creation returned null.");
            return new NativeResource<WgComputePipeline>(native, dependencies);
        }
        catch
        {
            foreach (var dependency in dependencies) dependency.Dispose();
            throw;
        }
    }

    public ComputePipelineHandle RegisterComputePipeline(NativeResource<WgComputePipeline> resource)
    {
        var (index, generation) = ComputePipelines.Add(resource.Native);
        var handle = new ComputePipelineHandle(index, generation);
        _computePipelineResources.Add(handle, resource);
        return handle;
    }

    public WgComputePipeline ResolveComputePipeline(ComputePipelineHandle h)
    {
        if (!ComputePipelines.TryGet(h.Index, h.Generation, out var pipeline))
            throw new StaleHandleException($"Compute pipeline handle ({h.Index},{h.Generation}) is stale or invalid.");
        return pipeline;
    }

    /// <summary>Invalidate the slot. Compute pipelines have no content cache, so detaching drops
    /// the only managed reference — safe: WebGPU pipelines have no explicit destroy, and Dawn
    /// refcounts in-flight GPU use.</summary>
    public bool DetachComputePipeline(ComputePipelineHandle h)
    {
        if (!ComputePipelines.Remove(h.Index, h.Generation)) return false;
        if (_computePipelineResources.Remove(h, out var resource)) resource.Dispose();
        return true;
    }

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

    /// <summary>Invalidates a pipeline slot; the renderer also releases its corresponding cache lease.</summary>
    public bool DetachPipeline(PipelineHandle h) => Pipelines.Remove(h.Index, h.Generation);

    /// <summary>Slang-output shader creation. Dedupes the native <see cref="WgShaderModule"/> by
    /// <c>(WGSL source, entry point, stage)</c> BELOW the public handle layer — the GPU compile
    /// is shared while references remain, but every call mints a fresh <see cref="ShaderHandle"/>.
    /// Matches the <see cref="PipelineCache"/> pattern: structural content dedupe under the hood,
    /// distinct public-handle identity above so two callers can independently destroy their
    /// handles without invalidating each other's still-live references to the shared native.</summary>
    public ShaderHandle CreateShaderModule(in ShaderModuleDesc moduleDesc)
    {
        var key = new ShaderModuleCacheKey(moduleDesc.Wgsl, moduleDesc.EntryPoint, moduleDesc.Stage);
        var lease = _shaderModuleCache.Acquire(key, () =>
        {
            var wgslDesc = new WgShaderModuleWGSLDescriptor { Code = key.Wgsl };
            return Device.CreateShaderModuleWGSL(key.EntryPoint, in wgslDesc)
                ?? throw new InvalidOperationException("ShaderModule creation returned null.");
        });
        try
        {
            var (index, generation) = Shaders.Add(lease.Value);
            var handle = new ShaderHandle(index, generation);
            _shaderModuleLeases.Add(handle, lease);
            return handle;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var resource in _computePipelineResources.Values) resource.Dispose();
        foreach (var layout in _bindGroupLayouts.Values) layout.Dispose();
        foreach (var shader in _shaderModuleLeases.Values) shader.Dispose();
        _computePipelineResources.Clear();
        _bindGroupLayouts.Clear();
        _shaderModuleLeases.Clear();
        Pipelines.Clear();
        ComputePipelines.Clear();
        BindGroups.Clear();
        Samplers.Clear();
        TextureViews.Clear();
        Textures.Clear();
        Buffers.Clear();
        Shaders.Clear();
        _shaderModuleCache.Clear();
        _bindGroupLayoutCache.Clear();
        // Destroy device-owned GPU resources even when a host retains NativeDevice. The safe
        // wrapper still owns the native handle; releasing that borrowed handle would double-free it.
        WebGpuSharp.Marshalling.WebGPUMarshal.GetHandle(Device).Destroy();
        GC.KeepAlive(Device);
    }

    // Both arrive from Dawn on a foreign thread. Their callers decode the UTF-8 message only
    // after an explicit IsEnabled check, because the decode — not the formatting — is what costs
    // something here, and [LoggerMessage] can only guard the latter.

    [LoggerMessage(EventId = 61, Level = LogLevel.Error, Message = "{ErrorType}: {Text}")]
    private static partial void LogUncapturedError(ILogger logger, WebGpuSharp.ErrorType errorType, string text);

    /// <remarks>Critical, not Error: an uncaptured error is one bad call, a lost device is the end
    /// of rendering for this process.</remarks>
    [LoggerMessage(EventId = 62, Level = LogLevel.Critical, Message = "device lost ({Reason}): {Text}")]
    private static partial void LogDeviceLost(ILogger logger, WebGpuSharp.DeviceLostReason reason, string text);
}

/// <summary>A texture slot entry: the native texture plus its default full view (the M2 scope
/// binds whole textures; per-mip/per-layer views come with offscreen targets later).</summary>
internal sealed record TextureEntry(WgTexture Texture, WgTextureView View);

internal static class VertexLayoutExtensions
{
    public static int SumAttributes(this ReadOnlySpan<VertexBufferLayoutDesc> layouts)
    {
        var n = 0;
        for (var i = 0; i < layouts.Length; i++) n += layouts[i].Attributes.Length;
        return n;
    }
}

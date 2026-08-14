using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Paradise.Rendering.Browser.Internal;

namespace Paradise.Rendering.Browser;

/// <summary>The browser <see cref="IRenderer"/> backend: drives the host browser's own WebGPU
/// implementation from WebAssembly through the <c>paradise-webgpu.js</c> shim this package ships.
/// Same contract as the desktop Dawn backend, so <c>PbrRenderer</c> and anything else written
/// against <see cref="IRenderer"/> runs unchanged in a browser tab.</summary>
/// <remarks>
/// <para>Construction is asynchronous (<see cref="CreateAsync"/>): a browser cannot block on
/// adapter/device acquisition, so the synchronous constructor the Dawn backend offers has no
/// counterpart here.</para>
/// <para>Handles follow the same stale-handle contract as the Dawn backend: a <c>Destroy*</c>
/// invalidates its handle synchronously and any later use throws <see cref="StaleHandleException"/>
/// rather than resolving to a recycled resource. There is no deferred-destruction queue — WebGPU's
/// JS API keeps a destroyed object's in-flight GPU work valid on its own, so releasing the slot
/// immediately is safe.</para>
/// <para>Everything here runs on the single browser thread that created the renderer.</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserRenderer : IRenderer, IDisposable
{
    private readonly ResourceTable _buffers = new();
    private readonly ResourceTable _textures = new();
    private readonly ResourceTable _textureViews = new();
    private readonly ResourceTable _samplers = new();
    private readonly ResourceTable _bindGroups = new();
    private readonly ResourceTable _pipelines = new();
    // Compute pipelines have their own slot space (slot index == JS table index, and JS keeps
    // render and compute pipelines in separate tables because setPipeline is type-checked).
    private readonly ResourceTable _computePipelines = new();

    // Native shader modules are deduped by WGSL content and never released: the same insert-only,
    // renderer-lifetime policy as the Dawn backend's module cache. A program compiled for both a
    // rigid and a skinned pipeline pays one browser-side compile, which matters because the PBR
    // WGSL is a few hundred kilobytes and each interop crossing copies the whole string.
    private readonly System.Collections.Generic.Dictionary<string, int> _shaderModules = new(StringComparer.Ordinal);
    private int _nextShaderModuleSlot;

    // Pipeline/pass depth compatibility is a WebGPU validation error, reported asynchronously
    // through the uncaptured-error event; this side table lets Submit raise it synchronously and
    // descriptively at SetPipeline time instead. Mirrors the Dawn backend.
    private readonly System.Collections.Generic.Dictionary<PipelineHandle, bool> _pipelineHasDepth = new();

    // Reusable upload staging so per-frame UpdateBuffer calls allocate nothing. Grown on demand.
    private byte[] _uploadStaging = new byte[4096];
    private bool _disposed;

    private BrowserRenderer(TextureFormat colorFormat, uint uniformAlignment, bool supportsBc, string adapterInfo, uint width, uint height)
    {
        ColorFormat = colorFormat;
        UniformBufferOffsetAlignment = uniformAlignment;
        SupportsBcTextureCompression = supportsBc;
        AdapterInfo = adapterInfo;
        Width = width;
        Height = height;
    }

    /// <summary>Import the shim, request an adapter and device, and configure
    /// <paramref name="canvasSelector"/>'s WebGPU context at
    /// <paramref name="width"/> x <paramref name="height"/>.</summary>
    /// <param name="canvasSelector">A CSS selector for the target <c>&lt;canvas&gt;</c>, e.g.
    /// <c>"#gpu-canvas"</c>.</param>
    /// <param name="moduleUrl">Where to load <c>paradise-webgpu.js</c> from. The default resolves
    /// to this package's static web asset, which the Razor SDK publishes into the consuming app —
    /// hosts that relocate or bundle the shim pass their own URL.</param>
    /// <exception cref="InvalidOperationException">The browser has no WebGPU support, no adapter
    /// was available, or the selector matched no canvas. The JS-side message is preserved.</exception>
    public static async Task<BrowserRenderer> CreateAsync(
        string canvasSelector, uint width, uint height, string moduleUrl = DefaultModuleUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(canvasSelector);
        await JSHost.ImportAsync(ModuleName, ResolveModuleUrl(moduleUrl)).ConfigureAwait(false);
        var format = await InitJs(canvasSelector, (int)width, (int)height).ConfigureAwait(false);
        return new BrowserRenderer(
            ParseFormat(format),
            // WebGPU guarantees the reported alignment is at most 256; clamping up keeps uniform
            // ring layouts identical across adapters (and matches the Dawn backend).
            Math.Max(256u, (uint)UniformAlignmentJs()),
            SupportsBcJs(),
            AdapterInfoJs(),
            Math.Max(1, width),
            Math.Max(1, height));
    }

    /// <summary>Make a module URL absolute against the page's base URI.</summary>
    /// <remarks>The runtime's <c>JSHost.ImportAsync</c> dynamic-imports the URL from inside
    /// <c>_framework/</c>, so a page-relative path like <c>_content/…</c> would resolve to
    /// <c>_framework/_content/…</c> and 404. Resolving against <c>document.baseURI</c> here means
    /// the default keeps working for apps served from a sub-path, which hardcoding a leading slash
    /// or a <c>../</c> would not.</remarks>
    private static string ResolveModuleUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)) return absolute.ToString();
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document");
        var baseUri = document?.GetPropertyAsString("baseURI");
        return baseUri is not null && Uri.TryCreate(new Uri(baseUri), url, out var resolved)
            ? resolved.ToString()
            : url;
    }

    /// <summary>The canvas context's preferred format — what pipeline color targets rendering to
    /// the backbuffer must use.</summary>
    public TextureFormat ColorFormat { get; }

    /// <summary>True when the adapter exposes <c>texture-compression-bc</c>. Browsers on Apple
    /// hardware never do, so browser hosts should expect the RGBA32 texture path.</summary>
    public bool SupportsBcTextureCompression { get; }

    /// <inheritdoc/>
    public uint UniformBufferOffsetAlignment { get; }

    /// <summary>Vendor/architecture/device string from <c>GPUAdapter.info</c>, for logging.</summary>
    public string AdapterInfo { get; }

    /// <summary>Current canvas size in device pixels.</summary>
    public uint Width { get; private set; }

    /// <summary>Current canvas size in device pixels.</summary>
    public uint Height { get; private set; }

    /// <summary>Resize the canvas and reconfigure its WebGPU context. Zero-sized requests clamp
    /// to 1, as on the Dawn backend.</summary>
    public void Resize(uint width, uint height)
    {
        ThrowIfDisposed();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        ResizeJs((int)width, (int)height);
    }

    /// <summary>The first WebGPU validation / device-lost message since the last call, or an empty
    /// string. Nothing in WebGPU surfaces these synchronously, so a host that never polls sees a
    /// broken frame as a plain clear colour; the sample polls once a second.</summary>
    public string TakeGpuError()
    {
        ThrowIfDisposed();
        return TakeErrorJs();
    }

    // -------- buffers --------

    /// <inheritdoc/>
    public BufferHandle CreateBuffer(in BufferDesc desc)
    {
        ThrowIfDisposed();
        var slot = _buffers.Allocate(out var generation);
        // queue.writeBuffer only accepts 4-byte-multiple sizes, so every buffer is rounded up at
        // creation: an upload whose payload is padded to 4 can then never run past the end.
        CreateBufferJs((int)slot, Align4(desc.Size), BufferUsageBits(desc.Usage), desc.Name ?? string.Empty);
        return new BufferHandle(slot, generation);
    }

    /// <inheritdoc/>
    public BufferHandle CreateBufferWithData<T>(in BufferDesc desc, ReadOnlySpan<T> data) where T : unmanaged
    {
        ThrowIfDisposed();
        // Widen before multiplying: the int product wraps at 2^31 for large uploads.
        var byteSize = (ulong)data.Length * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var sized = new BufferDesc(desc.Name, byteSize > desc.Size ? byteSize : desc.Size, desc.Usage | BufferUsage.CopyDst);
        var handle = CreateBuffer(in sized);
        UpdateBuffer(handle, 0, data);
        return handle;
    }

    /// <inheritdoc/>
    public void UpdateBuffer<T>(BufferHandle handle, ulong offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        ThrowIfDisposed();
        var index = _buffers.Resolve(handle.Index, handle.Generation, "Buffer");
        WriteBufferJs((int)index, offset, Stage(MemoryMarshal.AsBytes(data)));
    }

    /// <inheritdoc/>
    public void DestroyBuffer(BufferHandle handle)
    {
        ThrowIfDisposed();
        if (!_buffers.Release(handle.Index, handle.Generation)) return;
        DestroyBufferJs((int)handle.Index);
    }

    // -------- textures --------

    /// <inheritdoc/>
    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        ThrowIfDisposed();
        if (IsBcFormat(desc.Format) && !SupportsBcTextureCompression)
            throw new NotSupportedException(
                $"Texture format '{desc.Format}' requires the texture-compression-bc adapter feature, " +
                "which this browser's adapter did not grant. Check SupportsBcTextureCompression and " +
                "upload an RGBA fallback instead.");

        var json = new StringBuilder(192);
        json.Append("{\"label\":");
        AppendJsonString(json, desc.Name);
        json.Append(",\"width\":").Append(desc.Width)
            .Append(",\"height\":").Append(desc.Height)
            .Append(",\"layers\":").Append(Math.Max(1, desc.DepthOrArrayLayers))
            .Append(",\"mips\":").Append(Math.Max(1, desc.MipLevelCount))
            .Append(",\"samples\":").Append(Math.Max(1, desc.SampleCount))
            .Append(",\"dimension\":\"").Append(DimensionName(desc.Dimension))
            .Append("\",\"format\":\"").Append(FormatName(desc.Format))
            .Append("\",\"usage\":").Append(TextureUsageBits(desc.Usage))
            .Append('}');

        var slot = _textures.Allocate(out var generation);
        CreateTextureJs((int)slot, json.ToString());
        return new TextureHandle(slot, generation);
    }

    /// <inheritdoc/>
    public void WriteTexture(TextureHandle handle, uint mipLevel, ReadOnlySpan<byte> data, uint bytesPerRow, uint rowsPerImage, uint width, uint height)
    {
        ThrowIfDisposed();
        var index = _textures.Resolve(handle.Index, handle.Generation, "Texture");
        // No 4-byte rounding here: queue.writeTexture sizes the copy from the extent and row
        // pitch, and padding the source would only mask a short payload.
        WriteTextureJs(
            (int)index, (int)mipLevel, Stage(data, pad: false),
            (int)bytesPerRow, (int)rowsPerImage, (int)width, (int)height);
    }

    /// <inheritdoc/>
    public void DestroyTexture(TextureHandle handle)
    {
        ThrowIfDisposed();
        if (!_textures.Release(handle.Index, handle.Generation)) return;
        DestroyTextureJs((int)handle.Index);
    }

    /// <inheritdoc/>
    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        ThrowIfDisposed();
        var texture = _textures.Resolve(desc.Texture.Index, desc.Texture.Generation, "Texture");
        var slot = _textureViews.Allocate(out var generation);
        CreateTextureViewJs(
            (int)slot, (int)texture, ViewDimensionName(desc.Dimension),
            (int)desc.BaseArrayLayer, (int)Math.Max(1, desc.ArrayLayerCount), desc.Name ?? string.Empty);
        return new TextureViewHandle(slot, generation);
    }

    /// <inheritdoc/>
    public void DestroyTextureView(TextureViewHandle handle)
    {
        ThrowIfDisposed();
        if (!_textureViews.Release(handle.Index, handle.Generation)) return;
        DestroyTextureViewJs((int)handle.Index);
    }

    // -------- samplers --------

    /// <inheritdoc/>
    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        ThrowIfDisposed();
        var json = new StringBuilder(192);
        json.Append("{\"label\":");
        AppendJsonString(json, desc.Name);
        json.Append(",\"addressU\":\"").Append(AddressName(desc.AddressU))
            .Append("\",\"addressV\":\"").Append(AddressName(desc.AddressV))
            .Append("\",\"addressW\":\"").Append(AddressName(desc.AddressW))
            .Append("\",\"magFilter\":\"").Append(FilterName(desc.MagFilter))
            .Append("\",\"minFilter\":\"").Append(FilterName(desc.MinFilter))
            .Append("\",\"mipFilter\":\"").Append(FilterName(desc.MipmapFilter))
            .Append("\",\"maxAnisotropy\":").Append(Math.Max((ushort)1, desc.MaxAnisotropy));
        if (desc.Compare is { } compare)
            json.Append(",\"compare\":\"").Append(CompareName(compare)).Append('"');
        json.Append('}');

        var slot = _samplers.Allocate(out var generation);
        CreateSamplerJs((int)slot, json.ToString());
        return new SamplerHandle(slot, generation);
    }

    /// <inheritdoc/>
    public void DestroySampler(SamplerHandle handle)
    {
        ThrowIfDisposed();
        if (!_samplers.Release(handle.Index, handle.Generation)) return;
        DestroySamplerJs((int)handle.Index);
    }

    // -------- bind groups --------

    /// <inheritdoc/>
    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        ThrowIfDisposed();
        var layout = new StringBuilder(256);
        AppendGroupLayout(layout, desc.Layout);

        var entriesJson = new StringBuilder(256);
        entriesJson.Append('[');
        var entries = desc.Entries.Span;
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var e = ref entries[i];
            if (i > 0) entriesJson.Append(',');
            entriesJson.Append("{\"binding\":").Append(e.Binding).Append(",\"kind\":").Append((int)e.Kind).Append(",\"index\":");
            switch (e.Kind)
            {
                case BindGroupEntryKind.Buffer:
                    entriesJson.Append(_buffers.Resolve(e.Buffer.Index, e.Buffer.Generation, "Buffer"))
                               .Append(",\"offset\":").Append(e.Offset)
                               .Append(",\"size\":").Append(e.Size);
                    break;
                case BindGroupEntryKind.Texture:
                    entriesJson.Append(_textures.Resolve(e.Texture.Index, e.Texture.Generation, "Texture"));
                    break;
                case BindGroupEntryKind.Sampler:
                    entriesJson.Append(_samplers.Resolve(e.Sampler.Index, e.Sampler.Generation, "Sampler"));
                    break;
                case BindGroupEntryKind.TextureView:
                    entriesJson.Append(_textureViews.Resolve(e.View.Index, e.View.Generation, "TextureView"));
                    break;
                default:
                    throw new NotSupportedException($"Bind group entry kind '{e.Kind}' is not supported.");
            }
            entriesJson.Append('}');
        }
        entriesJson.Append(']');

        var slot = _bindGroups.Allocate(out var generation);
        CreateBindGroupJs((int)slot, layout.ToString(), entriesJson.ToString(), desc.Name ?? string.Empty);
        return new BindGroupHandle(slot, generation);
    }

    /// <inheritdoc/>
    public void DestroyBindGroup(BindGroupHandle handle)
    {
        ThrowIfDisposed();
        if (!_bindGroups.Release(handle.Index, handle.Generation)) return;
        DestroyBindGroupJs((int)handle.Index);
    }

    // -------- shared helpers --------

    private static bool IsBcFormat(TextureFormat format) =>
        format is >= TextureFormat.Bc1RgbaUnorm and <= TextureFormat.Bc7RgbaUnormSrgb;

    private static double Align4(ulong size) => (size + 3ul) & ~3ul;

    /// <summary>Copy <paramref name="data"/> into the reusable staging array and hand back the
    /// exact segment. The JS boundary copies whatever it is given, so staging keeps per-frame
    /// uploads allocation-free without copying more bytes than the payload needs.</summary>
    private ArraySegment<byte> Stage(ReadOnlySpan<byte> data, bool pad = true)
    {
        var length = pad ? (data.Length + 3) & ~3 : data.Length;
        if (_uploadStaging.Length < length)
            Array.Resize(ref _uploadStaging, Math.Max(length, _uploadStaging.Length * 2));
        data.CopyTo(_uploadStaging);
        // Zero the 1..3 padding bytes so a re-used staging buffer never uploads the previous
        // frame's tail into the padding.
        for (var i = data.Length; i < length; i++) _uploadStaging[i] = 0;
        return new ArraySegment<byte>(_uploadStaging, 0, length);
    }

    /// <summary>Canonical JSON for one bind-group layout. The JS side content-keys its
    /// GPUBindGroupLayout cache on this exact string, so a bind group built from the same layout
    /// content as a pipeline's group resolves to the identical GPU object — the compatibility rule
    /// made true by construction, as in the Dawn backend.</summary>
    private static void AppendGroupLayout(StringBuilder json, BindGroupLayoutDesc group)
    {
        json.Append('[');
        for (var i = 0; i < group.Entries.Length; i++)
        {
            var e = group.Entries[i];
            if (i > 0) json.Append(',');
            json.Append("{\"binding\":").Append(e.Binding)
                .Append(",\"visibility\":").Append((uint)e.Visibility); // engine bits == GPUShaderStage bits
            switch (e.Type)
            {
                case BindingResourceType.UniformBuffer:
                    AppendBufferBinding(json, "uniform", e);
                    break;
                case BindingResourceType.StorageBuffer:
                    AppendBufferBinding(json, "storage", e);
                    break;
                case BindingResourceType.ReadonlyStorageBuffer:
                    AppendBufferBinding(json, "read-only-storage", e);
                    break;
                case BindingResourceType.Sampler:
                    json.Append(",\"sampler\":{\"type\":\"filtering\"}");
                    break;
                case BindingResourceType.ComparisonSampler:
                    json.Append(",\"sampler\":{\"type\":\"comparison\"}");
                    break;
                case BindingResourceType.SampledTexture:
                    json.Append(",\"texture\":{\"sampleType\":\"float\",\"viewDimension\":\"2d\"}");
                    break;
                case BindingResourceType.UnfilterableFloatTexture:
                    json.Append(",\"texture\":{\"sampleType\":\"unfilterable-float\",\"viewDimension\":\"2d\"}");
                    break;
                case BindingResourceType.DepthTexture:
                    json.Append(",\"texture\":{\"sampleType\":\"depth\",\"viewDimension\":\"2d\"}");
                    break;
                case BindingResourceType.DepthTextureArray:
                    json.Append(",\"texture\":{\"sampleType\":\"depth\",\"viewDimension\":\"2d-array\"}");
                    break;
                case BindingResourceType.StorageTexture:
                    // Only StorageTexture entries emit the new key, so every pre-existing layout's
                    // JSON — and with it the JS-side layout-cache key — stays byte-identical.
                    if (e.StorageFormat == TextureFormat.Undefined)
                        throw new InvalidOperationException(
                            $"StorageTexture binding {e.Binding} has no StorageFormat — the layout cannot be built.");
                    json.Append(",\"storageTexture\":{\"access\":\"").Append(StorageAccessName(e.Access))
                        .Append("\",\"format\":\"").Append(FormatName(e.StorageFormat))
                        .Append("\",\"viewDimension\":\"2d\"}");
                    break;
                default:
                    throw new NotSupportedException($"Binding resource type '{e.Type}' is not supported.");
            }
            json.Append('}');
        }
        json.Append(']');
    }

    private static string StorageAccessName(StorageTextureAccess access) => access switch
    {
        StorageTextureAccess.WriteOnly => "write-only",
        StorageTextureAccess.ReadOnly => "read-only",
        StorageTextureAccess.ReadWrite => "read-write",
        _ => throw new NotSupportedException($"Storage texture access '{access}' has no WebGPU mapping."),
    };

    private static void AppendBufferBinding(StringBuilder json, string type, BindGroupLayoutEntryDesc entry)
    {
        json.Append(",\"buffer\":{\"type\":\"").Append(type).Append('"');
        if (entry.HasDynamicOffset) json.Append(",\"hasDynamicOffset\":true");
        if (entry.MinBufferSize > 0) json.Append(",\"minBindingSize\":").Append(entry.MinBufferSize);
        json.Append('}');
    }

    private static void AppendJsonString(StringBuilder json, string? value)
    {
        json.Append('"');
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Destroy the WebGPU device. Every handle issued by this renderer becomes unusable.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeJs();
    }
}

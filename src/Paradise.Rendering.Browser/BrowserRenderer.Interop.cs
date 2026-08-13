using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Paradise.Rendering.Browser;

/// <summary>The JS boundary: every <c>[JSImport]</c> binding into <c>paradise-webgpu.js</c>, plus
/// the enum-to-WebGPU-string mapping the descriptors are built from. Kept apart from the renderer
/// logic so the shim's surface can be read in one place — it is the file that must move in
/// lockstep with the JS module.</summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserRenderer
{
    /// <summary>The JS module name the <c>[JSImport]</c>s bind against — the same string
    /// <see cref="CreateAsync"/> passes to <c>JSHost.ImportAsync</c>.</summary>
    internal const string ModuleName = "paradise-webgpu";

    /// <summary>Default location of the shim inside a consuming WebAssembly app: the Razor SDK
    /// publishes this package's <c>wwwroot</c> there, so an app that references the package needs
    /// no JavaScript and no build step of its own.</summary>
    public const string DefaultModuleUrl = "./_content/Paradise.Rendering.Browser/paradise-webgpu.js";

    // ---- device / surface ----

    [JSImport("init", ModuleName)]
    private static partial Task<string> InitJs(string canvasSelector, int width, int height);

    [JSImport("uniformBufferOffsetAlignment", ModuleName)]
    private static partial int UniformAlignmentJs();

    [JSImport("supportsBcCompression", ModuleName)]
    private static partial bool SupportsBcJs();

    [JSImport("adapterInfo", ModuleName)]
    private static partial string AdapterInfoJs();

    [JSImport("resize", ModuleName)]
    private static partial void ResizeJs(int width, int height);

    [JSImport("takeError", ModuleName)]
    private static partial string TakeErrorJs();

    [JSImport("dispose", ModuleName)]
    private static partial void DisposeJs();

    // ---- resources ----

    [JSImport("createShaderModule", ModuleName)]
    private static partial void CreateShaderModuleJs(int slot, string wgsl, string label);

    [JSImport("createBuffer", ModuleName)]
    private static partial void CreateBufferJs(int slot, double size, int usage, string label);

    [JSImport("writeBuffer", ModuleName)]
    private static partial void WriteBufferJs(int index, double offset, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data);

    [JSImport("destroyBuffer", ModuleName)]
    private static partial void DestroyBufferJs(int index);

    [JSImport("createTexture", ModuleName)]
    private static partial void CreateTextureJs(int slot, string descJson);

    [JSImport("writeTexture", ModuleName)]
    private static partial void WriteTextureJs(
        int index, int mipLevel, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data,
        int bytesPerRow, int rowsPerImage, int width, int height);

    [JSImport("destroyTexture", ModuleName)]
    private static partial void DestroyTextureJs(int index);

    [JSImport("createTextureView", ModuleName)]
    private static partial void CreateTextureViewJs(
        int slot, int textureIndex, string dimension, int baseArrayLayer, int arrayLayerCount, string label);

    [JSImport("destroyTextureView", ModuleName)]
    private static partial void DestroyTextureViewJs(int index);

    [JSImport("createSampler", ModuleName)]
    private static partial void CreateSamplerJs(int slot, string descJson);

    [JSImport("destroySampler", ModuleName)]
    private static partial void DestroySamplerJs(int index);

    [JSImport("createBindGroup", ModuleName)]
    private static partial void CreateBindGroupJs(int slot, string layoutJson, string entriesJson, string label);

    [JSImport("destroyBindGroup", ModuleName)]
    private static partial void DestroyBindGroupJs(int index);

    [JSImport("createPipeline", ModuleName)]
    private static partial void CreatePipelineJs(int slot, string descJson);

    [JSImport("destroyPipeline", ModuleName)]
    private static partial void DestroyPipelineJs(int index);

    [JSImport("submitFrame", ModuleName)]
    private static partial void SubmitFrameJs(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> frame, int passCount, int opCount);

    // ---- enum mapping ----
    //
    // Explicit switches rather than ToString(): the WebGPU names are a wire contract, and a new
    // enum member must surface as a build break here, not as a string the browser rejects at
    // pipeline-creation time.

    private static string FormatName(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => "r8unorm",
        TextureFormat.Rgba8Unorm => "rgba8unorm",
        TextureFormat.Rgba8UnormSrgb => "rgba8unorm-srgb",
        TextureFormat.Bgra8Unorm => "bgra8unorm",
        TextureFormat.Bgra8UnormSrgb => "bgra8unorm-srgb",
        TextureFormat.Rgba16Float => "rgba16float",
        TextureFormat.Rgba32Float => "rgba32float",
        TextureFormat.Depth32Float => "depth32float",
        TextureFormat.Depth24PlusStencil8 => "depth24plus-stencil8",
        TextureFormat.Bc1RgbaUnorm => "bc1-rgba-unorm",
        TextureFormat.Bc1RgbaUnormSrgb => "bc1-rgba-unorm-srgb",
        TextureFormat.Bc3RgbaUnorm => "bc3-rgba-unorm",
        TextureFormat.Bc3RgbaUnormSrgb => "bc3-rgba-unorm-srgb",
        TextureFormat.Bc4RUnorm => "bc4-r-unorm",
        TextureFormat.Bc5RgUnorm => "bc5-rg-unorm",
        TextureFormat.Bc7RgbaUnorm => "bc7-rgba-unorm",
        TextureFormat.Bc7RgbaUnormSrgb => "bc7-rgba-unorm-srgb",
        _ => throw new NotSupportedException($"Texture format '{format}' has no WebGPU mapping."),
    };

    private static TextureFormat ParseFormat(string name) => name switch
    {
        "bgra8unorm" => TextureFormat.Bgra8Unorm,
        "rgba8unorm" => TextureFormat.Rgba8Unorm,
        "bgra8unorm-srgb" => TextureFormat.Bgra8UnormSrgb,
        "rgba8unorm-srgb" => TextureFormat.Rgba8UnormSrgb,
        _ => throw new NotSupportedException($"Unexpected preferred canvas format '{name}'."),
    };

    private static string DimensionName(TextureDimension dimension) => dimension switch
    {
        TextureDimension.D1 => "1d",
        TextureDimension.D2 => "2d",
        TextureDimension.D3 => "3d",
        _ => throw new NotSupportedException($"Texture dimension '{dimension}' has no WebGPU mapping."),
    };

    private static string ViewDimensionName(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.D2 => "2d",
        TextureViewDimension.D2Array => "2d-array",
        TextureViewDimension.Cube => "cube",
        _ => throw new NotSupportedException($"Texture view dimension '{dimension}' has no WebGPU mapping."),
    };

    private static string AddressName(SamplerAddressMode mode) => mode switch
    {
        SamplerAddressMode.Repeat => "repeat",
        SamplerAddressMode.MirrorRepeat => "mirror-repeat",
        SamplerAddressMode.ClampToEdge => "clamp-to-edge",
        _ => throw new NotSupportedException($"Sampler address mode '{mode}' has no WebGPU mapping."),
    };

    private static string FilterName(SamplerFilterMode mode) => mode switch
    {
        SamplerFilterMode.Nearest => "nearest",
        SamplerFilterMode.Linear => "linear",
        _ => throw new NotSupportedException($"Sampler filter mode '{mode}' has no WebGPU mapping."),
    };

    private static string CompareName(CompareFunction compare) => compare switch
    {
        CompareFunction.Never => "never",
        CompareFunction.Less => "less",
        CompareFunction.Equal => "equal",
        CompareFunction.LessEqual => "less-equal",
        CompareFunction.Greater => "greater",
        CompareFunction.NotEqual => "not-equal",
        CompareFunction.GreaterEqual => "greater-equal",
        CompareFunction.Always => "always",
        _ => throw new NotSupportedException($"Compare function '{compare}' has no WebGPU mapping."),
    };

    private static string TopologyName(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => "point-list",
        PrimitiveTopology.LineList => "line-list",
        PrimitiveTopology.LineStrip => "line-strip",
        PrimitiveTopology.TriangleList => "triangle-list",
        PrimitiveTopology.TriangleStrip => "triangle-strip",
        _ => throw new NotSupportedException($"Primitive topology '{topology}' has no WebGPU mapping."),
    };

    private static string VertexFormatName(VertexFormat format) => format switch
    {
        VertexFormat.Float32 => "float32",
        VertexFormat.Float32x2 => "float32x2",
        VertexFormat.Float32x3 => "float32x3",
        VertexFormat.Float32x4 => "float32x4",
        VertexFormat.Uint8x4 => "uint8x4",
        VertexFormat.Unorm8x4 => "unorm8x4",
        VertexFormat.Sint16x2 => "sint16x2",
        VertexFormat.Snorm16x2 => "snorm16x2",
        VertexFormat.Uint16x2 => "uint16x2",
        VertexFormat.Uint16x4 => "uint16x4",
        VertexFormat.Sint32 => "sint32",
        VertexFormat.Sint32x2 => "sint32x2",
        VertexFormat.Sint32x3 => "sint32x3",
        VertexFormat.Sint32x4 => "sint32x4",
        VertexFormat.Uint32 => "uint32",
        VertexFormat.Uint32x2 => "uint32x2",
        VertexFormat.Uint32x3 => "uint32x3",
        VertexFormat.Uint32x4 => "uint32x4",
        _ => throw new NotSupportedException($"Vertex format '{format}' has no WebGPU mapping."),
    };

    // GPUBufferUsage bits. CopyDst is always granted: every upload path in this backend goes
    // through queue.writeBuffer, which requires it.
    private static int BufferUsageBits(BufferUsage usage)
    {
        var bits = 0x8; // COPY_DST
        if ((usage & BufferUsage.CopySrc) != 0) bits |= 0x4;
        if ((usage & BufferUsage.Index) != 0) bits |= 0x10;
        if ((usage & BufferUsage.Vertex) != 0) bits |= 0x20;
        if ((usage & BufferUsage.Uniform) != 0) bits |= 0x40;
        if ((usage & BufferUsage.Storage) != 0) bits |= 0x80;
        if ((usage & BufferUsage.Indirect) != 0) bits |= 0x100;
        return bits;
    }

    // GPUTextureUsage bits.
    private static int TextureUsageBits(TextureUsage usage)
    {
        var bits = 0;
        if ((usage & TextureUsage.CopySrc) != 0) bits |= 0x1;
        if ((usage & TextureUsage.CopyDst) != 0) bits |= 0x2;
        if ((usage & TextureUsage.TextureBinding) != 0) bits |= 0x4;
        if ((usage & TextureUsage.StorageBinding) != 0) bits |= 0x8;
        if ((usage & TextureUsage.RenderAttachment) != 0) bits |= 0x10;
        return bits;
    }
}

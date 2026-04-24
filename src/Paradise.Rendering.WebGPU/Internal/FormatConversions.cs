using System;
using WgVertexFormat = WebGpuSharp.VertexFormat;
using WgPrimitiveTopology = WebGpuSharp.PrimitiveTopology;
using WgIndexFormat = WebGpuSharp.IndexFormat;
using WgVertexStepMode = WebGpuSharp.VertexStepMode;
using WgBufferUsage = WebGpuSharp.BufferUsage;
using WgTextureFormat = WebGpuSharp.TextureFormat;
using WgTextureUsage = WebGpuSharp.TextureUsage;
using WgTextureDimension = WebGpuSharp.TextureDimension;
using WgTextureViewDimension = WebGpuSharp.TextureViewDimension;
using WgTextureAspect = WebGpuSharp.TextureAspect;
using WgAddressMode = WebGpuSharp.AddressMode;
using WgFilterMode = WebGpuSharp.FilterMode;
using WgMipmapFilterMode = WebGpuSharp.MipmapFilterMode;
using WgCompareFunction = WebGpuSharp.CompareFunction;
using WgLoadOp = WebGpuSharp.LoadOp;
using WgStoreOp = WebGpuSharp.StoreOp;
using WgShaderStage = WebGpuSharp.ShaderStage;

namespace Paradise.Rendering.WebGPU.Internal;

/// <summary>1:1 mapping helpers between <see cref="Paradise.Rendering"/>'s public enum surface
/// and WebGPUSharp's enums. Centralized so the conversions are testable and round-trip is obvious;
/// avoids scattering `(WgVertexFormat)(int)format` tricks that would silently break when WebGPUSharp
/// reorders its enum values.</summary>
internal static class FormatConversions
{
    public static WgVertexFormat ToWgpu(VertexFormat f) => f switch
    {
        VertexFormat.Float32 => WgVertexFormat.Float32,
        VertexFormat.Float32x2 => WgVertexFormat.Float32x2,
        VertexFormat.Float32x3 => WgVertexFormat.Float32x3,
        VertexFormat.Float32x4 => WgVertexFormat.Float32x4,
        VertexFormat.Uint8x4 => WgVertexFormat.Uint8x4,
        VertexFormat.Unorm8x4 => WgVertexFormat.Unorm8x4,
        VertexFormat.Sint16x2 => WgVertexFormat.Sint16x2,
        VertexFormat.Snorm16x2 => WgVertexFormat.Snorm16x2,
        VertexFormat.Uint16x2 => WgVertexFormat.Uint16x2,
        VertexFormat.Uint16x4 => WgVertexFormat.Uint16x4,
        VertexFormat.Sint32 => WgVertexFormat.Sint32,
        VertexFormat.Sint32x2 => WgVertexFormat.Sint32x2,
        VertexFormat.Sint32x3 => WgVertexFormat.Sint32x3,
        VertexFormat.Sint32x4 => WgVertexFormat.Sint32x4,
        VertexFormat.Uint32 => WgVertexFormat.Uint32,
        VertexFormat.Uint32x2 => WgVertexFormat.Uint32x2,
        VertexFormat.Uint32x3 => WgVertexFormat.Uint32x3,
        VertexFormat.Uint32x4 => WgVertexFormat.Uint32x4,
        _ => throw new NotSupportedException($"Vertex format '{f}' has no WebGPU mapping."),
    };

    public static WgPrimitiveTopology ToWgpu(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.PointList => WgPrimitiveTopology.PointList,
        PrimitiveTopology.LineList => WgPrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => WgPrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleList => WgPrimitiveTopology.TriangleList,
        PrimitiveTopology.TriangleStrip => WgPrimitiveTopology.TriangleStrip,
        _ => throw new NotSupportedException($"Topology '{t}' has no WebGPU mapping."),
    };

    public static WgIndexFormat ToWgpu(IndexFormat f) => f switch
    {
        IndexFormat.Uint16 => WgIndexFormat.Uint16,
        IndexFormat.Uint32 => WgIndexFormat.Uint32,
        _ => throw new NotSupportedException($"Index format '{f}' has no WebGPU mapping."),
    };

    public static WgVertexStepMode ToWgpu(VertexStepMode m) => m switch
    {
        VertexStepMode.Vertex => WgVertexStepMode.Vertex,
        VertexStepMode.Instance => WgVertexStepMode.Instance,
        _ => throw new NotSupportedException($"Step mode '{m}' has no WebGPU mapping."),
    };

    public static WgBufferUsage ToWgpu(BufferUsage u)
    {
        var w = WgBufferUsage.None;
        if ((u & BufferUsage.Vertex) != 0) w |= WgBufferUsage.Vertex;
        if ((u & BufferUsage.Index) != 0) w |= WgBufferUsage.Index;
        if ((u & BufferUsage.Uniform) != 0) w |= WgBufferUsage.Uniform;
        if ((u & BufferUsage.Storage) != 0) w |= WgBufferUsage.Storage;
        if ((u & BufferUsage.CopySrc) != 0) w |= WgBufferUsage.CopySrc;
        if ((u & BufferUsage.CopyDst) != 0) w |= WgBufferUsage.CopyDst;
        if ((u & BufferUsage.Indirect) != 0) w |= WgBufferUsage.Indirect;
        return w;
    }

    public static WgTextureFormat ToWgpu(TextureFormat f) => f switch
    {
        TextureFormat.Undefined => WgTextureFormat.Undefined,
        TextureFormat.R8Unorm => WgTextureFormat.R8Unorm,
        TextureFormat.Rgba8Unorm => WgTextureFormat.RGBA8Unorm,
        TextureFormat.Rgba8UnormSrgb => WgTextureFormat.RGBA8UnormSrgb,
        TextureFormat.Bgra8Unorm => WgTextureFormat.BGRA8Unorm,
        TextureFormat.Bgra8UnormSrgb => WgTextureFormat.BGRA8UnormSrgb,
        TextureFormat.Depth32Float => WgTextureFormat.Depth32Float,
        TextureFormat.Depth24PlusStencil8 => WgTextureFormat.Depth24PlusStencil8,
        _ => throw new NotSupportedException($"Texture format '{f}' has no WebGPU mapping."),
    };

    public static TextureFormat FromWgpu(WgTextureFormat f) => f switch
    {
        WgTextureFormat.Undefined => TextureFormat.Undefined,
        WgTextureFormat.R8Unorm => TextureFormat.R8Unorm,
        WgTextureFormat.RGBA8Unorm => TextureFormat.Rgba8Unorm,
        WgTextureFormat.RGBA8UnormSrgb => TextureFormat.Rgba8UnormSrgb,
        WgTextureFormat.BGRA8Unorm => TextureFormat.Bgra8Unorm,
        WgTextureFormat.BGRA8UnormSrgb => TextureFormat.Bgra8UnormSrgb,
        WgTextureFormat.Depth32Float => TextureFormat.Depth32Float,
        WgTextureFormat.Depth24PlusStencil8 => TextureFormat.Depth24PlusStencil8,
        // Symmetric with every sibling helper in this file — unknown values must throw, not
        // silently map to Undefined. WebGpuRenderer.ColorFormat calls FromWgpu on the swapchain
        // format, so an HDR-capable surface (RGBA16Float, etc.) or a future WebGPUSharp value we
        // haven't mapped will surface here as a descriptive exception instead of producing an
        // Undefined pipeline color format that Dawn later rejects with an opaque error.
        _ => throw new NotSupportedException($"WebGPU texture format '{f}' has no Paradise.Rendering mapping."),
    };

    public static WgTextureUsage ToWgpu(TextureUsage u)
    {
        var w = WgTextureUsage.None;
        if ((u & TextureUsage.CopySrc) != 0) w |= WgTextureUsage.CopySrc;
        if ((u & TextureUsage.CopyDst) != 0) w |= WgTextureUsage.CopyDst;
        if ((u & TextureUsage.TextureBinding) != 0) w |= WgTextureUsage.TextureBinding;
        if ((u & TextureUsage.StorageBinding) != 0) w |= WgTextureUsage.StorageBinding;
        if ((u & TextureUsage.RenderAttachment) != 0) w |= WgTextureUsage.RenderAttachment;
        return w;
    }

    public static WgTextureDimension ToWgpu(TextureDimension d) => d switch
    {
        TextureDimension.D1 => WgTextureDimension.D1,
        TextureDimension.D2 => WgTextureDimension.D2,
        TextureDimension.D3 => WgTextureDimension.D3,
        _ => throw new NotSupportedException($"Texture dimension '{d}' has no WebGPU mapping."),
    };

    public static WgTextureViewDimension ToWgpu(TextureViewDimension d) => d switch
    {
        TextureViewDimension.Undefined => WgTextureViewDimension.Undefined,
        TextureViewDimension.D1 => WgTextureViewDimension.D1,
        TextureViewDimension.D2 => WgTextureViewDimension.D2,
        TextureViewDimension.D2Array => WgTextureViewDimension.D2Array,
        TextureViewDimension.Cube => WgTextureViewDimension.Cube,
        TextureViewDimension.CubeArray => WgTextureViewDimension.CubeArray,
        TextureViewDimension.D3 => WgTextureViewDimension.D3,
        _ => throw new NotSupportedException($"Texture view dimension '{d}' has no WebGPU mapping."),
    };

    public static WgTextureAspect ToWgpu(TextureAspect a) => a switch
    {
        TextureAspect.All => WgTextureAspect.All,
        TextureAspect.StencilOnly => WgTextureAspect.StencilOnly,
        TextureAspect.DepthOnly => WgTextureAspect.DepthOnly,
        _ => throw new NotSupportedException($"Texture aspect '{a}' has no WebGPU mapping."),
    };

    public static WgAddressMode ToWgpu(SamplerAddressMode m) => m switch
    {
        SamplerAddressMode.Repeat => WgAddressMode.Repeat,
        SamplerAddressMode.MirrorRepeat => WgAddressMode.MirrorRepeat,
        SamplerAddressMode.ClampToEdge => WgAddressMode.ClampToEdge,
        _ => throw new NotSupportedException($"Sampler address mode '{m}' has no WebGPU mapping."),
    };

    public static WgFilterMode ToWgpuFilter(SamplerFilterMode m) => m switch
    {
        SamplerFilterMode.Nearest => WgFilterMode.Nearest,
        SamplerFilterMode.Linear => WgFilterMode.Linear,
        _ => throw new NotSupportedException($"Sampler filter mode '{m}' has no WebGPU mapping."),
    };

    public static WgMipmapFilterMode ToWgpuMipmapFilter(SamplerFilterMode m) => m switch
    {
        SamplerFilterMode.Nearest => WgMipmapFilterMode.Nearest,
        SamplerFilterMode.Linear => WgMipmapFilterMode.Linear,
        _ => throw new NotSupportedException($"Sampler mipmap filter mode '{m}' has no WebGPU mapping."),
    };

    public static WgCompareFunction ToWgpu(CompareFunction f) => f switch
    {
        CompareFunction.Never => WgCompareFunction.Never,
        CompareFunction.Less => WgCompareFunction.Less,
        CompareFunction.Equal => WgCompareFunction.Equal,
        CompareFunction.LessEqual => WgCompareFunction.LessEqual,
        CompareFunction.Greater => WgCompareFunction.Greater,
        CompareFunction.NotEqual => WgCompareFunction.NotEqual,
        CompareFunction.GreaterEqual => WgCompareFunction.GreaterEqual,
        CompareFunction.Always => WgCompareFunction.Always,
        _ => throw new NotSupportedException($"Compare function '{f}' has no WebGPU mapping."),
    };

    public static WgLoadOp ToWgpu(LoadOp o) => o switch
    {
        LoadOp.Load => WgLoadOp.Load,
        LoadOp.Clear => WgLoadOp.Clear,
        _ => throw new NotSupportedException($"LoadOp '{o}' has no WebGPU mapping."),
    };

    public static WgStoreOp ToWgpu(StoreOp o) => o switch
    {
        StoreOp.Store => WgStoreOp.Store,
        StoreOp.Discard => WgStoreOp.Discard,
        _ => throw new NotSupportedException($"StoreOp '{o}' has no WebGPU mapping."),
    };

    public static WgShaderStage ToWgpu(ShaderStage s)
    {
        var w = WgShaderStage.None;
        if ((s & ShaderStage.Vertex) != 0) w |= WgShaderStage.Vertex;
        if ((s & ShaderStage.Fragment) != 0) w |= WgShaderStage.Fragment;
        if ((s & ShaderStage.Compute) != 0) w |= WgShaderStage.Compute;
        return w;
    }
}

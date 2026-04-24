using System;

namespace Paradise.Rendering;

/// <summary>Native windowing platform of a <see cref="SurfaceDescriptor"/>. Selects which native handle the backend consumes.</summary>
public enum SurfacePlatform : byte
{
    Unknown = 0,
    Win32,
    Xlib,
    Wayland,
    Cocoa,
    Headless,
}

/// <summary>How a buffer may be used by the GPU. Combine with bitwise OR.</summary>
[Flags]
public enum BufferUsage : uint
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    CopySrc = 1 << 4,
    CopyDst = 1 << 5,
    Indirect = 1 << 6,
}

/// <summary>Pixel formats supported by textures and render attachments. Mirrors WebGPU's GPUTextureFormat subset used by the engine.</summary>
public enum TextureFormat : uint
{
    Undefined = 0,
    R8Unorm,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Bgra8Unorm,
    Bgra8UnormSrgb,
    Depth32Float,
    Depth24PlusStencil8,
}

/// <summary>How a texture may be used by the GPU. Combine with bitwise OR.</summary>
[Flags]
public enum TextureUsage : uint
{
    None = 0,
    CopySrc = 1 << 0,
    CopyDst = 1 << 1,
    TextureBinding = 1 << 2,
    StorageBinding = 1 << 3,
    RenderAttachment = 1 << 4,
}

/// <summary>Texture dimensionality.</summary>
public enum TextureDimension : byte
{
    D1 = 0,
    D2,
    D3,
}

/// <summary>How sampler reads beyond [0,1) are wrapped.</summary>
public enum SamplerAddressMode : byte
{
    Repeat = 0,
    MirrorRepeat,
    ClampToEdge,
}

/// <summary>Sampler filtering for magnification, minification, and mip selection.</summary>
public enum SamplerFilterMode : byte
{
    Nearest = 0,
    Linear,
}

/// <summary>Shader stages a binding or shader module is visible to. Combine with bitwise OR.</summary>
[Flags]
public enum ShaderStage : uint
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
}

/// <summary>Primitive assembly mode for a render pipeline.</summary>
public enum PrimitiveTopology : byte
{
    PointList = 0,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
}

/// <summary>Index buffer element width.</summary>
public enum IndexFormat : byte
{
    Uint16 = 0,
    Uint32,
}

/// <summary>What to do with the existing attachment contents at the start of a render pass.</summary>
public enum LoadOp : byte
{
    Load = 0,
    Clear,
}

/// <summary>What to do with the rendered attachment contents at the end of a render pass.</summary>
public enum StoreOp : byte
{
    Store = 0,
    Discard,
}

/// <summary>Vertex attribute element layout. Mirrors WebGPU's GPUVertexFormat.</summary>
public enum VertexFormat : uint
{
    Undefined = 0,
    Float32,
    Float32x2,
    Float32x3,
    Float32x4,
    Uint8x4,
    Unorm8x4,
    Sint16x2,
    Snorm16x2,
    Uint16x2,
    Uint16x4,
    Sint32,
    Sint32x2,
    Sint32x3,
    Sint32x4,
    Uint32,
    Uint32x2,
    Uint32x3,
    Uint32x4,
}

/// <summary>How a vertex buffer is stepped through during a draw — per vertex or per instance.</summary>
public enum VertexStepMode : byte
{
    Vertex = 0,
    Instance,
}

/// <summary>Type of resource referenced by a bind group layout entry.</summary>
public enum BindingResourceType : byte
{
    UniformBuffer = 0,
    StorageBuffer,
    ReadonlyStorageBuffer,
    Sampler,
    ComparisonSampler,
    SampledTexture,
    MultisampledTexture,
    StorageTexture,
}

/// <summary>Dimensionality of a texture view. Mirrors WebGPU's GPUTextureViewDimension.</summary>
public enum TextureViewDimension : byte
{
    Undefined = 0,
    D1,
    D2,
    D2Array,
    Cube,
    CubeArray,
    D3,
}

/// <summary>Which planes of a texture a view targets. Mirrors WebGPU's GPUTextureAspect.</summary>
public enum TextureAspect : byte
{
    All = 0,
    StencilOnly,
    DepthOnly,
}

/// <summary>Depth/stencil comparison function. Mirrors WebGPU's GPUCompareFunction.</summary>
public enum CompareFunction : byte
{
    Never = 0,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always,
}

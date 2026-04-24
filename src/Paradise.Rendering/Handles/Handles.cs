using System.Runtime.InteropServices;

namespace Paradise.Rendering;

// Handle structs reserve 16 bytes via [StructLayout(Size = 16)] to leave room for backend-specific
// packing (type tag, slot generation widening, etc.) without an ABI break later. Identity is the
// (Index, Generation) pair only — the trailing 8 bytes are intentional padding and excluded from
// equality/hashing by record-struct synthesis (it only considers declared positional members).

/// <summary>Opaque handle to a backend GPU buffer. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct BufferHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly BufferHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU texture. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct TextureHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly TextureHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU sampler. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct SamplerHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly SamplerHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU pipeline state object. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct PipelineHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly PipelineHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU shader module. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct ShaderHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly ShaderHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU texture view used as a render attachment or as a sampled
/// texture binding. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct RenderViewHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly RenderViewHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU bind group layout. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct BindGroupLayoutHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly BindGroupLayoutHandle Invalid = default;
}

/// <summary>Opaque handle to a backend GPU bind group (a resolved resource binding). Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct BindGroupHandle(uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public static readonly BindGroupHandle Invalid = default;
}

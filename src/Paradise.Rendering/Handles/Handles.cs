using System;
using System.Runtime.InteropServices;

namespace Paradise.Rendering;

// Handle structs reserve 16 bytes via [StructLayout(Size = 16)] to leave room for backend-specific
// packing (type tag, slot generation widening, etc.) without an ABI break later. Identity is the
// (Index, Generation) pair only — the trailing 8 bytes are intentional padding and excluded from
// equality and hashing.

/// <summary>Opaque handle to a backend GPU buffer. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct BufferHandle : IEquatable<BufferHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public BufferHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly BufferHandle Invalid = default;

    public bool Equals(BufferHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is BufferHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(BufferHandle left, BufferHandle right) => left.Equals(right);
    public static bool operator !=(BufferHandle left, BufferHandle right) => !left.Equals(right);
}

/// <summary>Opaque handle to a backend GPU texture. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct TextureHandle : IEquatable<TextureHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public TextureHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly TextureHandle Invalid = default;

    public bool Equals(TextureHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is TextureHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(TextureHandle left, TextureHandle right) => left.Equals(right);
    public static bool operator !=(TextureHandle left, TextureHandle right) => !left.Equals(right);
}

/// <summary>Opaque handle to a backend GPU sampler. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct SamplerHandle : IEquatable<SamplerHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public SamplerHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly SamplerHandle Invalid = default;

    public bool Equals(SamplerHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is SamplerHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(SamplerHandle left, SamplerHandle right) => left.Equals(right);
    public static bool operator !=(SamplerHandle left, SamplerHandle right) => !left.Equals(right);
}

/// <summary>Opaque handle to a backend GPU pipeline state object. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct PipelineHandle : IEquatable<PipelineHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public PipelineHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly PipelineHandle Invalid = default;

    public bool Equals(PipelineHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is PipelineHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(PipelineHandle left, PipelineHandle right) => left.Equals(right);
    public static bool operator !=(PipelineHandle left, PipelineHandle right) => !left.Equals(right);
}

/// <summary>Opaque handle to a backend GPU shader module. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct ShaderHandle : IEquatable<ShaderHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public ShaderHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly ShaderHandle Invalid = default;

    public bool Equals(ShaderHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is ShaderHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(ShaderHandle left, ShaderHandle right) => left.Equals(right);
    public static bool operator !=(ShaderHandle left, ShaderHandle right) => !left.Equals(right);
}

/// <summary>Opaque handle to a backend GPU texture view used as a render attachment. Default value is invalid.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct RenderViewHandle : IEquatable<RenderViewHandle>
{
    public readonly uint Index;
    public readonly uint Generation;

    public RenderViewHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsValid => Generation != 0;
    public static readonly RenderViewHandle Invalid = default;

    public bool Equals(RenderViewHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is RenderViewHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(RenderViewHandle left, RenderViewHandle right) => left.Equals(right);
    public static bool operator !=(RenderViewHandle left, RenderViewHandle right) => !left.Equals(right);
}

using System;

namespace Paradise.Rendering;

/// <summary>One buffer-valued entry in a <see cref="BindGroupDesc"/>. <see cref="Size"/> is the
/// bound sub-range length in bytes; pass <c>0</c> to bind the buffer's full remaining range from
/// <see cref="Offset"/> (mirrors WebGPU's "size = WHOLE_SIZE" convention).</summary>
public readonly record struct BindGroupBufferEntry(
    uint Binding,
    BufferHandle Buffer,
    ulong Offset,
    ulong Size);

/// <summary>One texture-view-valued entry in a <see cref="BindGroupDesc"/>.</summary>
public readonly record struct BindGroupTextureEntry(
    uint Binding,
    RenderViewHandle View);

/// <summary>One sampler-valued entry in a <see cref="BindGroupDesc"/>.</summary>
public readonly record struct BindGroupSamplerEntry(
    uint Binding,
    SamplerHandle Sampler);

/// <summary>Creation parameters for a GPU bind group. Split by resource kind so each entry's
/// binding slot can be typed at the API surface rather than a tagged union. The three spans may be
/// non-overlapping (a binding slot appears in at most one of buffers/textures/samplers).</summary>
public readonly struct BindGroupDesc
{
    public string? Name { get; init; }
    public BindGroupLayoutHandle Layout { get; init; }
    public ReadOnlyMemory<BindGroupBufferEntry> Buffers { get; init; }
    public ReadOnlyMemory<BindGroupTextureEntry> Textures { get; init; }
    public ReadOnlyMemory<BindGroupSamplerEntry> Samplers { get; init; }
}

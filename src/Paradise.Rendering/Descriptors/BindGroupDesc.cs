using System;

namespace Paradise.Rendering;

/// <summary>Which resource field of a <see cref="BindGroupEntryDesc"/> is live.</summary>
public enum BindGroupEntryKind : byte
{
    Buffer = 0,
    Texture,
    Sampler,
}

/// <summary>One resource bound into a bind group. Flat struct (no boxing): <see cref="Kind"/>
/// selects which handle field is meaningful; construct via the factory methods.</summary>
public readonly record struct BindGroupEntryDesc(
    uint Binding,
    BindGroupEntryKind Kind,
    BufferHandle Buffer,
    ulong Offset,
    ulong Size,
    TextureHandle Texture,
    SamplerHandle Sampler)
{
    /// <summary>Bind a buffer range. <paramref name="size"/> is the bound window (for a
    /// dynamic-offset entry this is the per-draw stride window, not the whole buffer).</summary>
    public static BindGroupEntryDesc ForBuffer(uint binding, BufferHandle buffer, ulong offset, ulong size) =>
        new(binding, BindGroupEntryKind.Buffer, buffer, offset, size, default, default);

    public static BindGroupEntryDesc ForTexture(uint binding, TextureHandle texture) =>
        new(binding, BindGroupEntryKind.Texture, default, 0, 0, texture, default);

    public static BindGroupEntryDesc ForSampler(uint binding, SamplerHandle sampler) =>
        new(binding, BindGroupEntryKind.Sampler, default, 0, 0, default, sampler);
}

/// <summary>Creation parameters for a bind group: the single group layout it instantiates plus
/// the resources for each of that layout's bindings. The backend resolves the layout through its
/// content-keyed bind-group-layout cache, so a bind group built from the same
/// <see cref="BindGroupLayoutDesc"/> content as a pipeline's group is guaranteed compatible.</summary>
public readonly record struct BindGroupDesc(
    string? Name,
    BindGroupLayoutDesc Layout,
    ReadOnlyMemory<BindGroupEntryDesc> Entries);

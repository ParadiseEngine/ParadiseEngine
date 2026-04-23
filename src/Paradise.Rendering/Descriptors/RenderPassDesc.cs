using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Render pass descriptor with up to <see cref="MaxColorAttachments"/> color attachments
/// and an optional depth attachment. Color attachments live inline (no heap allocation); use the
/// <see cref="this[int]"/> indexer or <see cref="ColorAttachments"/> span — both honor
/// <see cref="ColorAttachmentCount"/>.</summary>
/// <remarks>This is a mutable struct: it must be passed by <c>ref</c> when mutated. Storing one in a
/// <see cref="System.Collections.Generic.List{T}"/> field, capturing it as a property getter copy,
/// or assigning to a local before writing through the indexer will silently lose writes to the copy.</remarks>
public struct RenderPassDesc
{
    /// <summary>Maximum number of color attachments per pass. Matches WebGPU's required minimum (8).</summary>
    public const int MaxColorAttachments = 8;

    /// <summary>Raw inline storage for the eight color-attachment slots. Prefer the count-aware
    /// <see cref="this[int]"/> indexer or <see cref="ColorAttachments"/> span — both bound writes
    /// to <see cref="ColorAttachmentCount"/>. Direct field access is exposed for backends that
    /// need uniform layout-based marshalling.</summary>
    public ColorAttachmentBuffer Colors;

    public int ColorAttachmentCount;
    public DepthAttachmentDesc? Depth;

    public RenderPassDesc(int colorAttachmentCount, DepthAttachmentDesc? depth = null)
    {
        if (colorAttachmentCount < 0 || colorAttachmentCount > MaxColorAttachments)
            throw new ArgumentOutOfRangeException(nameof(colorAttachmentCount));
        Colors = default;
        ColorAttachmentCount = colorAttachmentCount;
        Depth = depth;
    }

    /// <summary>Count-aware color attachment accessor: bounds-checked against
    /// <see cref="ColorAttachmentCount"/>, not just <see cref="MaxColorAttachments"/>. Writes via
    /// this indexer are visible to <see cref="ColorAttachments"/>.</summary>
    public ref ColorAttachmentDesc this[int index]
    {
        [UnscopedRef]
        get
        {
            if ((uint)index >= (uint)ColorAttachmentCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref Unsafe.Add(ref Unsafe.As<ColorAttachmentBuffer, ColorAttachmentDesc>(ref Colors), index);
        }
    }

    /// <summary>Live span over the color attachment storage, sized by <see cref="ColorAttachmentCount"/>.</summary>
    [UnscopedRef]
    public Span<ColorAttachmentDesc> ColorAttachments =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<ColorAttachmentBuffer, ColorAttachmentDesc>(ref Colors), ColorAttachmentCount);
}

/// <summary>Inline storage for up to <see cref="RenderPassDesc.MaxColorAttachments"/> color
/// attachments inside a <see cref="RenderPassDesc"/>. Sequential layout is required — the
/// surrounding indexer and span use <see cref="Unsafe.Add{T}(ref T, int)"/> over <see cref="Slot0"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ColorAttachmentBuffer
{
    public ColorAttachmentDesc Slot0;
    public ColorAttachmentDesc Slot1;
    public ColorAttachmentDesc Slot2;
    public ColorAttachmentDesc Slot3;
    public ColorAttachmentDesc Slot4;
    public ColorAttachmentDesc Slot5;
    public ColorAttachmentDesc Slot6;
    public ColorAttachmentDesc Slot7;
}

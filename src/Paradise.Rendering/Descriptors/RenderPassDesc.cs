using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Render pass descriptor with up to <see cref="MaxColorAttachments"/> color attachments
/// and an optional depth attachment. Color attachments live inline (no heap allocation); use
/// <see cref="ColorAttachments"/> to read/write a span over the live storage.</summary>
public struct RenderPassDesc
{
    /// <summary>Maximum number of color attachments per pass. Matches WebGPU's required minimum (8).</summary>
    public const int MaxColorAttachments = 8;

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

    /// <summary>Live span over the color attachment storage, sized by <see cref="ColorAttachmentCount"/>.</summary>
    [UnscopedRef]
    public Span<ColorAttachmentDesc> ColorAttachments =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<ColorAttachmentBuffer, ColorAttachmentDesc>(ref Colors), ColorAttachmentCount);
}

/// <summary>Inline storage for up to <see cref="RenderPassDesc.MaxColorAttachments"/> color attachments
/// inside a <see cref="RenderPassDesc"/>. Access through <see cref="RenderPassDesc.ColorAttachments"/>.</summary>
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

    /// <summary>Indexer over the inline slots. Bounds-checked against <see cref="RenderPassDesc.MaxColorAttachments"/>.</summary>
    public ref ColorAttachmentDesc this[int index]
    {
        [UnscopedRef]
        get
        {
            if ((uint)index >= (uint)RenderPassDesc.MaxColorAttachments)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref Unsafe.Add(ref Slot0, index);
        }
    }
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering;

/// <summary>Stores up to MaxColorAttachments inline and an optional depth attachment.</summary>
/// <remarks>Use the count-aware indexer or ColorAttachments span. Mutations must reach the original
/// struct by ref; changing a copy does not update the stored pass.</remarks>
public struct RenderPassDesc
{
    /// <summary>Maximum number of color attachments per pass. Matches WebGPU's required minimum (8).</summary>
    public const int MaxColorAttachments = 8;

    /// <summary>Exposes raw inline attachment storage for backend marshalling.</summary>
    /// <remarks>Prefer the count-aware indexer or ColorAttachments span; slots beyond
    /// ColorAttachmentCount are not submitted.</remarks>
    public ColorAttachmentBuffer Colors;

    private int _colorAttachmentCount;

    /// <summary>Number of valid entries in <see cref="Colors"/>. Setter validates against
    /// <c>[0, <see cref="MaxColorAttachments"/>]</c> so the count cannot be raised to enable
    /// out-of-bounds reads via <see cref="this[int]"/> or <see cref="ColorAttachments"/>.</summary>
    public int ColorAttachmentCount
    {
        readonly get => _colorAttachmentCount;
        set
        {
            if ((uint)value > (uint)MaxColorAttachments)
                throw new ArgumentOutOfRangeException(nameof(value));
            _colorAttachmentCount = value;
        }
    }

    public DepthAttachmentDesc? Depth;

    public RenderPassDesc(int colorAttachmentCount, DepthAttachmentDesc? depth = null)
    {
        if ((uint)colorAttachmentCount > (uint)MaxColorAttachments)
            throw new ArgumentOutOfRangeException(nameof(colorAttachmentCount));
        Colors = default;
        _colorAttachmentCount = colorAttachmentCount;
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
            if ((uint)index >= (uint)_colorAttachmentCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref Unsafe.Add(ref Unsafe.As<ColorAttachmentBuffer, ColorAttachmentDesc>(ref Colors), index);
        }
    }

    /// <summary>Live span over the color attachment storage, sized by <see cref="ColorAttachmentCount"/>.</summary>
    [UnscopedRef]
    public Span<ColorAttachmentDesc> ColorAttachments =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<ColorAttachmentBuffer, ColorAttachmentDesc>(ref Colors), _colorAttachmentCount);
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

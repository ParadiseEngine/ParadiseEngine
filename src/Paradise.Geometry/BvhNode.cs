using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Geometry;

/// <summary>One 8-wide node of a <see cref="WideBvh"/>, in the exact layout the compute tracer
/// reads (<c>Common/bvh.slang</c>: 96 bytes, 16-byte aligned fields).
///
/// <para>Child bounds are quantized to 8 bits per axis against the node's own bounds: the node
/// stores its origin and one power-of-two scale per axis, each child six bytes. That is the
/// format decision the plan fixed on day one — a node that decodes to floats on the GPU rather
/// than storing them costs a fifth of the memory reads of a full-float wide node, and the
/// traversal is bound by those reads. Quantized bounds are conservative: a child's decoded box
/// always contains its exact box, so the hierarchy never misses a hit; it only tests a few more.</para>
///
/// <para>Internal children are contiguous from <see cref="ChildBase"/>; the items of leaf children
/// (triangles, or instances in a top-level hierarchy) are contiguous from <see cref="LeafBase"/>.
/// Each child's <see cref="Meta"/> word says which it is and where it sits in those runs.</para></summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct BvhNode
{
    public const int ChildCount = 8;

    /// <summary>A meta word of zero: no child in this slot.</summary>
    public const ushort EmptyChild = 0;

    /// <summary>Meta bit: the child is an internal node at <see cref="ChildBase"/> + (meta &amp; 7).</summary>
    public const ushort InternalFlag = 0x4000;

    /// <summary>Meta bit: the child is a leaf of (meta &gt;&gt; 8 &amp; 0x3F) items starting at
    /// <see cref="LeafBase"/> + (meta &amp; 0xFF).</summary>
    public const ushort LeafFlag = 0x8000;

    /// <summary>Items one leaf child may hold. Bounded by the meta word's count field.</summary>
    public const int MaxLeafItems = 63;

    /// <summary>The quantization origin: the node's own minimum corner.</summary>
    public Vector3 Origin;

    /// <summary>Per-axis biased float exponents, one byte each (x, y, z): the quantization step of
    /// an axis is <c>2^(e - 127)</c>, the float whose bits are <c>e &lt;&lt; 23</c>.</summary>
    public uint Exponents;

    public uint ChildBase;
    public uint LeafBase;
    private uint _pad0;
    private uint _pad1;

    /// <summary>Eight 16-bit child meta words, two per uint, child 0 in the low half.</summary>
    public ChildMetaWords Meta;

    /// <summary>Quantized child minima on x (words 0, 1) and y (words 2, 3): eight bytes per axis,
    /// child 0 in the low byte of the axis's first word.</summary>
    public QuantizedXY LoXY;

    /// <summary>Quantized child maxima on x and y, laid out like <see cref="LoXY"/>.</summary>
    public QuantizedXY HiXY;

    /// <summary>Quantized child minima on z (two words).</summary>
    public QuantizedZ LoZ;

    /// <summary>Quantized child maxima on z.</summary>
    public QuantizedZ HiZ;

    [InlineArray(4)]
    public struct ChildMetaWords
    {
        private uint _element0;
    }

    // The xy words and the z words are separate fields so the WGSL struct — where a four-word
    // vector aligns to 16 bytes and a two-word one to 8 — packs into the same 96 bytes with no
    // padding: uint4 meta, uint4 loXY, uint4 hiXY, uint2 loZ, uint2 hiZ.
    [InlineArray(4)]
    public struct QuantizedXY
    {
        private uint _element0;
    }

    [InlineArray(2)]
    public struct QuantizedZ
    {
        private uint _element0;
    }

    public ushort GetMeta(int child) => (ushort)(Meta[child >> 1] >> ((child & 1) * 16));

    public void SetMeta(int child, ushort value)
    {
        var shift = (child & 1) * 16;
        ref var word = ref Meta[child >> 1];
        word = (word & ~(0xFFFFu << shift)) | ((uint)value << shift);
    }

    public static bool IsLeaf(ushort meta) => (meta & LeafFlag) != 0;
    public static bool IsInternal(ushort meta) => (meta & InternalFlag) != 0;
    public static int LeafItemCount(ushort meta) => (meta >> 8) & 0x3F;
    public static int LeafItemOffset(ushort meta) => meta & 0xFF;
    public static int InternalSlot(ushort meta) => meta & 0x7;

    public static ushort LeafMeta(int itemOffset, int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(itemOffset, 0xFF);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(itemCount, MaxLeafItems);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);
        return (ushort)(LeafFlag | (itemCount << 8) | itemOffset);
    }

    public static ushort InternalMeta(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, ChildCount);
        return (ushort)(InternalFlag | slot);
    }

    /// <summary>The quantized minimum of <paramref name="child"/> on <paramref name="axis"/>.</summary>
    public readonly byte GetLo(int axis, int child) =>
        (byte)(WordOf(in LoXY, in LoZ, axis, child) >> ((child & 3) * 8));

    public readonly byte GetHi(int axis, int child) =>
        (byte)(WordOf(in HiXY, in HiZ, axis, child) >> ((child & 3) * 8));

    public void SetLo(int axis, int child, byte value) => SetByte(ref LoXY, ref LoZ, axis, child, value);

    public void SetHi(int axis, int child, byte value) => SetByte(ref HiXY, ref HiZ, axis, child, value);

    private static uint WordOf(in QuantizedXY xy, in QuantizedZ z, int axis, int child) =>
        axis == 2 ? z[child >> 2] : xy[axis * 2 + (child >> 2)];

    private static void SetByte(ref QuantizedXY xy, ref QuantizedZ z, int axis, int child, byte value)
    {
        var shift = (child & 3) * 8;
        ref var word = ref axis == 2 ? ref z[child >> 2] : ref xy[axis * 2 + (child >> 2)];
        word = (word & ~(0xFFu << shift)) | ((uint)value << shift);
    }

    /// <summary>The quantization step of <paramref name="axis"/>.</summary>
    public readonly float Scale(int axis) => BitConverter.UInt32BitsToSingle(((Exponents >> (axis * 8)) & 0xFF) << 23);

    /// <summary>Decode one child's conservative bounds — the same arithmetic the shader performs.</summary>
    public readonly Aabb ChildBounds(int child)
    {
        var scale = new Vector3(Scale(0), Scale(1), Scale(2));
        var lo = new Vector3(GetLo(0, child), GetLo(1, child), GetLo(2, child));
        var hi = new Vector3(GetHi(0, child), GetHi(1, child), GetHi(2, child));
        return new Aabb(Origin + lo * scale, Origin + hi * scale);
    }
}

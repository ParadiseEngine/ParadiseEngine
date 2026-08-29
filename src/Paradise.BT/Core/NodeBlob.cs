using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// The shared, immutable half of a compiled tree, as a Paradise.BLOB asset: per node, where its
/// subtree ends, which type it is, where its data starts, and what that data reads as before
/// anything ticks.
///
/// Shaped after EntitiesBT's <c>NodeBlob</c>, minus its runtime arrays — a Paradise instance keeps
/// its states and live data in caller-owned spans (see <see cref="UnmanagedNodeBlob"/>) so it can
/// be an ECS component, rather than in a per-entity clone of the blob.
///
/// Every offset here is RELATIVE to the field holding it, so the whole thing is one position-
/// independent byte block: copyable, and readable wherever it lands.
///
/// <b>Never take this struct by value, by <c>in</c>, or by <c>ref readonly</c>.</b> Each
/// <see cref="BlobArray{T}"/> finds its payload at an offset from its own address, so a copy —
/// including the defensive copy the compiler makes when a non-readonly member is called on a
/// readonly reference — points its arrays at whatever follows the copy. That is why nothing here
/// is marked <c>readonly</c>: access it through a <see cref="NodeBlob"/><c>*</c> and the arrays
/// resolve against the real block.
/// </summary>
public struct NodeBlob
{
    /// <summary>Bumped when the layout of this struct changes.
    /// Version 2: added <see cref="Guids"/>, which is what made the blob itself loadable.</summary>
    public const int CurrentFormatVersion = 2;

    public int FormatVersion;

    /// <summary>Where each node's subtree ends, exclusive — the tree's topology, flattened.</summary>
    public BlobArray<int> EndIndices;

    /// <summary><see cref="NodeTypeRegistry"/> ids, which are assigned in registration order and
    /// so are valid only for the process that built this blob. A blob loaded from bytes carries a
    /// dead process's ids here until <see cref="BehaviorTreeLayout.Deserialize"/> rewrites them
    /// from <see cref="Guids"/> — which is why nothing may read them before it does.</summary>
    public BlobArray<int> Types;

    /// <summary>Each node type's <c>[Guid]</c> — the identity that survives a process, where
    /// <see cref="Types"/> does not. This array is what lets the layout blob be saved and loaded
    /// directly (EntitiesBT ships its blob as the asset too, but keys it on a 32-bit hash of the
    /// GUID; the full GUID costs 12 more bytes per node and cannot collide).</summary>
    public BlobArray<Guid> Guids;

    /// <summary>Where each node's data starts, with <c>Count + 1</c> entries so node <c>i</c>'s
    /// reserved size is <c>Offsets[i + 1] - Offsets[i]</c> without a second array.</summary>
    public BlobArray<int> Offsets;

    /// <summary>The authored defaults, laid out at <see cref="Offsets"/>. An instance starts as a
    /// copy of this and a reset restores from it.</summary>
    public BlobArray<byte> DefaultData;

    public int Count => Types.Length;

    /// <summary>How many bytes one instance's runtime data needs — the defaults' own size,
    /// padding included.</summary>
    public int RuntimeDataSize => DefaultData.Length;

    /// <summary>How many bytes <paramref name="count"/> nodes occupy from
    /// <paramref name="startNodeIndex"/> — the RESERVED span, so it includes the padding that
    /// keeps each node's data aligned.</summary>
    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        Offsets[startNodeIndex + count] - Offsets[startNodeIndex];
}

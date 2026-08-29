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
    /// v2: added <see cref="Guids"/>. v3: Guids deduplicated per type. v4: <see cref="Types"/>
    /// holds table indices, and the blob carries NOTHING process-local — a node's type is a GUID,
    /// full stop, and <see cref="NodeTypeRegistry"/> dispatches by it directly.</summary>
    public const int CurrentFormatVersion = 4;

    public int FormatVersion;

    /// <summary>Where each node's subtree ends, exclusive — the tree's topology, flattened.</summary>
    public BlobArray<int> EndIndices;

    /// <summary>Each node's index into <see cref="Guids"/> — ONE meaning, at rest and in
    /// memory.</summary>
    public BlobArray<int> Types;

    /// <summary>Each distinct node TYPE's <c>[Guid]</c>, ordered by first appearance — the
    /// identity that survives a process.</summary>
    public BlobArray<Guid> Guids;

    /// <summary>A node's durable type identity.</summary>
    public Guid TypeGuid(int nodeIndex) => Guids[Types[nodeIndex]];

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

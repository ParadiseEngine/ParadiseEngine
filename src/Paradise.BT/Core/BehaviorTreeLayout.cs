using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// The shared, immutable half of a compiled tree: topology, GUID table, offsets and authored
/// defaults, in one native blob every <see cref="BehaviorTreeRef"/> points into. A thousand agents share
/// one layout and each owns only its mutable half. Dispose it only once nothing still ticks
/// against it.
/// </summary>
public sealed class BehaviorTreeLayout : IDisposable
{
    private NativeBlobAssetReference<LayoutBlob> _blob;

    internal BehaviorTreeLayout(NativeBlobAssetReference<LayoutBlob> blob)
    {
        _blob = blob;
    }

    public ref LayoutBlob Blob => ref _blob.Value;

    public void Dispose()
    {
        _blob.Dispose();
        _blob = null!;
    }

    /// <summary>
    /// The layout's storage format, as a Paradise.BLOB asset: per node, where its subtree ends,
    /// which type it is, where its data starts, and what that data reads as before anything
    /// ticks.
    /// </summary>
    public struct LayoutBlob
    {
        public BlobArray<int> EndIndices;

        /// <summary>Each node's index into <see cref="Guids"/></summary>
        public BlobArray<int> Types;

        /// <summary>Each distinct node TYPE's <c>[Guid]</c>, ordered by first appearance</summary>
        public BlobArray<Guid> Guids;

        public Guid TypeGuid(int nodeIndex) => Guids[Types[nodeIndex]];

        /// <summary>Where each node's data starts, with <c>Count + 1</c> entries so node
        /// <c>i</c>'s reserved size is <c>Offsets[i + 1] - Offsets[i]</c> without a second
        /// array.</summary>
        public BlobArray<int> Offsets;

        /// <summary>The authored defaults, laid out at <see cref="Offsets"/>. An instance starts
        /// as a copy of this and a reset restores from it.</summary>
        public BlobArray<byte> DefaultData;

        public int Count => Types.Length;

        /// <summary>How many bytes one instance's runtime data needs — the defaults' own size,
        /// padding included.</summary>
        public int DataSize => DefaultData.Length;

        /// <summary>How many bytes <paramref name="count"/> nodes occupy from
        /// <paramref name="startNodeIndex"/> — the RESERVED span, so it includes the padding that
        /// keeps each node's data aligned.</summary>
        public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
            Offsets[startNodeIndex + count] - Offsets[startNodeIndex];
    }
}

using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>Owns the shared tree topology, GUID table, offsets and defaults in one native blob.</summary>
/// <remarks>Dispose only after every instance using this layout has finished.</remarks>
public sealed class BehaviorTreeLayout : IDisposable
{
    private NativeBlobAssetReference<LayoutBlob>? _blob;

    internal BehaviorTreeLayout(NativeBlobAssetReference<LayoutBlob> blob)
    {
        _blob = blob;
    }

    public ref LayoutBlob Blob
    {
        get
        {
            ObjectDisposedException.ThrowIf(_blob is null, this);
            return ref _blob.Value;
        }
    }

    public void Dispose()
    {
        _blob?.Dispose();
        _blob = null;
    }

    /// <summary>Stores node topology, types, aligned offsets and authored defaults in one blob.</summary>
    /// <remarks>Access this header by mutable reference so its arrays retain valid relative pointers.</remarks>
    public struct LayoutBlob
    {
        public BlobArray<int> EndIndices;

        /// <summary>Each node's index into <see cref="Guids"/>.</summary>
        public BlobArray<int> Types;

        /// <summary>Each distinct node type's <c>[Guid]</c>, ordered by first appearance.</summary>
        public BlobArray<Guid> Guids;

        public Guid TypeGuid(int nodeIndex) => Guids[Types[nodeIndex]];

        /// <summary>Where each node's data starts, with <c>Count + 1</c> entries so node
        /// <c>i</c>'s reserved size is <c>Offsets[i + 1] - Offsets[i]</c> without a second
        /// array.</summary>
        public BlobArray<int> Offsets;

        /// <summary>The authored defaults, laid out at <see cref="Offsets"/>.</summary>
        /// <remarks>Instance initialization and reset copy these bytes into runtime storage.</remarks>
        public BlobArray<byte> DefaultData;

        public int Count => Types.Length;

        /// <summary>The runtime storage size in bytes, including alignment padding.</summary>
        public int DataSize => DefaultData.Length;

        /// <summary>How many bytes <paramref name="count"/> nodes occupy from
        /// <paramref name="startNodeIndex"/>, including the padding that
        /// keeps each node's data aligned.</summary>
        public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
            Offsets[startNodeIndex + count] - Offsets[startNodeIndex];
    }
}

/// <summary>Identifies the tree type used to validate blackboards on the typed tick path.</summary>
/// <remarks>Only <c>BehaviorTrees.Compile&lt;TTree&gt;</c> creates a layout for <typeparamref name="TTree"/>.</remarks>
public readonly struct BehaviorTreeLayout<TTree> : IDisposable
{
    internal BehaviorTreeLayout(BehaviorTreeLayout untyped) => Untyped = untyped;

    public BehaviorTreeLayout Untyped { get; }

    /// <summary>The typed tickable view over caller-owned buffers.</summary>
    public BehaviorTreeRef<TTree> Ref(Span<NodeState> states, Span<byte> runtime)
        => new(new BehaviorTreeRef(ref Untyped.Blob, states, runtime));

    public void Dispose() => Untyped?.Dispose();
}

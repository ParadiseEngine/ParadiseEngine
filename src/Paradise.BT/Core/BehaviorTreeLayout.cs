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
    /// <summary>Alignment of the blob and of each array within it. Individual nodes pack at
    /// their own natural alignment (see <see cref="IRuntimeNodeFactory.DataAlignment"/>), capped
    /// by this.</summary>
    private const int DataAlignment = 16;

    private NativeBlobAssetReference<LayoutBlob> _blob;

    private BehaviorTreeLayout(NativeBlobAssetReference<LayoutBlob> blob)
    {
        _blob = blob;
    }

    public BehaviorTreeInstance CreateInstance() => new(this);
    public ref LayoutBlob Blob => ref _blob.Value;

    internal static BehaviorTreeLayout Build(ReadOnlySpan<BehaviorTreeNode> compiledNodes)
    {
        int count = compiledNodes.Length;
        var typeIds = new int[count];
        var endIndices = new int[count];
        var offsets = new int[count + 1];

        // One GUID per distinct type, ordered by first appearance. Types stores the table INDEX.
        var guidTable = new List<Guid>();
        var tableIndexByGuid = new Dictionary<Guid, int>();

        int size = 0;
        for (int i = 0; i < count; i++)
        {
            IRuntimeNodeFactory factory = compiledNodes[i].Factory;
            if (!NodeTypeRegistry.IsRegistered(factory.NodeGuid))
            {
                throw new InvalidOperationException(
                    $"Node type '{factory.NodeType.FullName}' (GUID '{factory.NodeGuid}') "
                    + "is not registered. Node types register themselves through the module "
                    + "initializer the BT generator emits, so this usually means the declaring "
                    + "project does not reference Paradise.BT.Generators as an analyzer, or the "
                    + $"type is not visible to it. Call {nameof(NodeTypeRegistry)}.Register<T>() "
                    + "explicitly for such a node.");
            }

            if (!tableIndexByGuid.TryGetValue(factory.NodeGuid, out int tableIndex))
            {
                tableIndex = guidTable.Count;
                tableIndexByGuid[factory.NodeGuid] = tableIndex;
                guidTable.Add(factory.NodeGuid);
            }

            typeIds[i] = tableIndex;
            size = Align(size, Math.Min(factory.DataAlignment, DataAlignment));
            offsets[i] = size;
            size += factory.DataSize;
        }

        offsets[count] = size;

        // Padding between nodes stays zeroed, which is what a fresh array gives us.
        var defaultData = new byte[size];
        for (int i = 0; i < count; i++)
        {
            BehaviorTreeNode node = compiledNodes[i];
            endIndices[i] = node.EndIndex;
            node.Factory.WriteDefaultData(
                defaultData.AsSpan(offsets[i], node.Factory.DataSize));
        }

        // Aligning every array to DataAlignment aligns the START of the one that follows it, which
        // is how DefaultData ends up on a 16-byte boundary within the blob. Per-node offsets then
        // respect each node's own alignment relative to that start, so a node's data is aligned in
        // the shared block and identically placed in whatever buffer an instance copies it into.
        var builder = new StructBuilder<LayoutBlob>();
        builder.SetArray(ref builder.Value.EndIndices, endIndices, DataAlignment);
        builder.SetArray(ref builder.Value.Types, typeIds, DataAlignment);
        builder.SetArray(ref builder.Value.Guids, guidTable, DataAlignment);
        builder.SetArray(ref builder.Value.Offsets, offsets, DataAlignment);
        builder.SetArray(ref builder.Value.DefaultData, defaultData, DataAlignment);

        return new BehaviorTreeLayout(
            builder.CreateNativeBlobAssetReference(DataAlignment));
    }

    private static int Align(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);

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

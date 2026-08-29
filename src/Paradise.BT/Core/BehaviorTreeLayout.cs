using Paradise.BLOB;

namespace Paradise.BT;

/// <summary>
/// Owns the <see cref="NodeBlob"/> a compiled tree ticks against.
///
/// A thousand agents share one layout; each owns only the mutable half (a
/// <see cref="NodeState"/> per node plus a copy of the node data), which is small and blittable
/// enough for an ECS component.
///
/// The blob lives in unmanaged memory that every <see cref="UnmanagedNodeBlob"/> points into:
/// dispose it only once nothing is still ticking against it.
///
/// Node types must be registered with <see cref="NodeTypeRegistry"/> first; an unregistered one is
/// refused by name here rather than faulting on the first tick.
/// </summary>
public sealed unsafe class BehaviorTreeLayout : IDisposable
{
    /// <summary>Alignment of the blob and of each array within it. Individual nodes pack at
    /// their own natural alignment (see <see cref="IRuntimeNodeFactory.DataAlignment"/>), capped
    /// by this.</summary>
    private const int DataAlignment = 16;

    private NativeBlobAssetReference<NodeBlob>? _blob;

    private BehaviorTreeLayout(NativeBlobAssetReference<NodeBlob> blob)
    {
        _blob = blob;
    }

    /// <summary>What an instance points at. Copyable, unmanaged, and valid only while this object
    /// is alive and undisposed.</summary>
    public BehaviorTreeLayoutHandle Handle =>
        _blob is null
            ? throw new ObjectDisposedException(nameof(BehaviorTreeLayout))
            : new(_blob.UnsafePtr);

    /// <summary>How many nodes the tree has — the length of an instance's state buffer.</summary>
    public int NodeCount => Handle.NodeCount;

    /// <summary>How many bytes an instance's runtime node data needs.</summary>
    public int RuntimeDataSize => Handle.RuntimeDataSize;

    /// <summary>A new instance over this layout. The instance roots this layout, so the native
    /// memory outlives every instance created this way; dispose still rests with whoever built
    /// the layout.</summary>
    public BehaviorTreeInstance CreateInstance() => new(Handle, owner: this);

    /// <summary>
    /// Flatten a compiled tree into a <see cref="NodeBlob"/>. Defaults are copied from the tree's
    /// own factories, so there is no second source of them.
    /// </summary>
    /// <exception cref="InvalidOperationException">A node's type was never registered with
    /// <see cref="NodeTypeRegistry"/>, or it holds managed references and cannot be stored as
    /// bytes.</exception>
    public static BehaviorTreeLayout Build(BehaviorTree tree)
    {
        ThrowHelper.ThrowIfNull(tree, nameof(tree));

        int count = tree.Count;
        var typeIds = new int[count];
        var endIndices = new int[count];
        var offsets = new int[count + 1];

        // One GUID per distinct type, ordered by first appearance, so serialization is
        // deterministic. Types stores the table INDEX.
        var guidTable = new List<Guid>();
        var tableIndexByGuid = new Dictionary<Guid, int>();

        int size = 0;
        for (int i = 0; i < count; i++)
        {
            IRuntimeNodeFactory factory = tree.GetCompiledNode(i).Factory;
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
            BehaviorTreeNode node = tree.GetCompiledNode(i);
            endIndices[i] = node.EndIndex;
            node.Factory.WriteDefaultData(
                defaultData.AsSpan(offsets[i], node.Factory.DataSize));
        }

        // Aligning every array to DataAlignment aligns the START of the one that follows it, which
        // is how DefaultData ends up on a 16-byte boundary within the blob. Per-node offsets then
        // respect each node's own alignment relative to that start, so a node's data is aligned in
        // the shared block and identically placed in whatever buffer an instance copies it into.
        var builder = new StructBuilder<NodeBlob>();
        builder.SetValue(ref builder.Value.FormatVersion, NodeBlob.CurrentFormatVersion);
        builder.SetArray(ref builder.Value.EndIndices, endIndices, DataAlignment);
        builder.SetArray(ref builder.Value.Types, typeIds, DataAlignment);
        builder.SetArray(ref builder.Value.Guids, guidTable, DataAlignment);
        builder.SetArray(ref builder.Value.Offsets, offsets, DataAlignment);
        builder.SetArray(ref builder.Value.DefaultData, defaultData, DataAlignment);

        return new BehaviorTreeLayout(
            builder.CreateNativeBlobAssetReference<NodeBlob>(DataAlignment));
    }

    private static int Align(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);

    /// <summary>
    /// The blob as bytes — a PURE raw copy, valid because every offset in a <see cref="NodeBlob"/>
    /// is self-relative and the blob carries nothing process-local: a node's type is its GUID,
    /// which is also what dispatch resolves by. The same tree serializes identically everywhere
    /// and a layout can be content-hashed.
    /// </summary>
    public byte[] SerializeToBytes()
    {
        NativeBlobAssetReference<NodeBlob> reference =
            _blob ?? throw new ObjectDisposedException(nameof(BehaviorTreeLayout));
        var bytes = new byte[reference.Length];
        new ReadOnlySpan<byte>(reference.UnsafePtr, reference.Length).CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Load a layout serialized by <see cref="SerializeToBytes"/>: copy into aligned native
    /// memory and validate it, including that every node type's GUID is registered with this
    /// process's <see cref="NodeTypeRegistry"/>. The caller owns the result and disposes it once
    /// nothing ticks against it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bytes are not a current-format layout blob,
    /// the topology is corrupt, or a node type is not registered in this process.</exception>
    public static BehaviorTreeLayout Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(NodeBlob))
        {
            throw new InvalidOperationException(
                $"Serialized behavior tree layout is too small ({data.Length} bytes) to hold a "
                + $"{nameof(NodeBlob)} header.");
        }

        var reference = new NativeBlobAssetReference<NodeBlob>(data, DataAlignment);
        try
        {
            Validate(reference.UnsafePtr, reference.Length);
        }
        catch
        {
            reference.Dispose();
            throw;
        }

        return new BehaviorTreeLayout(reference);
    }

    /// <summary>Refuse a corrupt blob at load rather than fault on the first tick. The blob itself
    /// is never written — a loaded blob stays byte-identical to the file.</summary>
    private static void Validate(NodeBlob* blob, int blobLength)
    {
        if (blob->FormatVersion != NodeBlob.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported behavior tree layout format version {blob->FormatVersion}. "
                + $"Expected {NodeBlob.CurrentFormatVersion}.");
        }

        ValidateArrayBounds(blob, blobLength, ref blob->EndIndices, nameof(NodeBlob.EndIndices));
        ValidateArrayBounds(blob, blobLength, ref blob->Types, nameof(NodeBlob.Types));
        ValidateArrayBounds(blob, blobLength, ref blob->Guids, nameof(NodeBlob.Guids));
        ValidateArrayBounds(blob, blobLength, ref blob->Offsets, nameof(NodeBlob.Offsets));
        ValidateArrayBounds(blob, blobLength, ref blob->DefaultData, nameof(NodeBlob.DefaultData));

        int count = blob->Types.Length;
        int typeCount = blob->Guids.Length;
        if (count == 0
            || blob->EndIndices.Length != count
            || typeCount == 0
            || typeCount > count
            || blob->Offsets.Length != count + 1)
        {
            throw new InvalidOperationException(
                "Serialized behavior tree layout is corrupt: its arrays disagree on the node "
                + $"count ({count} types, {typeCount} GUID table entries, "
                + $"{blob->EndIndices.Length} end indices, {blob->Offsets.Length} offsets).");
        }

        if (blob->Offsets[0] != 0 || blob->Offsets[count] != blob->DefaultData.Length)
        {
            throw new InvalidOperationException(
                "Serialized behavior tree layout is corrupt: node data offsets do not span the "
                + "default data block.");
        }

        if (blob->EndIndices[0] != count)
        {
            throw new InvalidOperationException(
                "Serialized behavior tree layout is corrupt: the root's subtree does not span "
                + "the whole tree.");
        }

        for (int i = 0; i < count; i++)
        {
            if (blob->Offsets[i + 1] < blob->Offsets[i] || blob->EndIndices[i] <= i || blob->EndIndices[i] > count)
            {
                throw new InvalidOperationException(
                    $"Serialized behavior tree layout is corrupt at node {i}: offsets must be "
                    + "non-decreasing and every subtree must end after its node, within the tree.");
            }
        }

        // Check the table once — T lookups for N nodes — so a per-node check below is an index.
        var seenGuids = new HashSet<Guid>();
        for (int t = 0; t < typeCount; t++)
        {
            Guid guid = blob->Guids[t];
            if (!seenGuids.Add(guid))
            {
                throw new InvalidOperationException(
                    $"Serialized behavior tree layout is corrupt: GUID '{guid}' appears twice in "
                    + "the type table, which a built layout never produces.");
            }

            if (!NodeTypeRegistry.IsRegistered(guid))
            {
                throw new InvalidOperationException(
                    $"Node GUID '{guid}' is not registered in this process. Every node type in a "
                    + $"serialized layout must be registered with {nameof(NodeTypeRegistry)} "
                    + "before the layout is loaded.");
            }
        }

        for (int i = 0; i < count; i++)
        {
            int tableIndex = blob->Types[i];
            if ((uint)tableIndex >= (uint)typeCount)
            {
                throw new InvalidOperationException(
                    $"Serialized behavior tree layout is corrupt: node {i} names type table "
                    + $"entry {tableIndex}, and the table has {typeCount}.");
            }

            Guid guid = blob->Guids[tableIndex];
            if (NodeTypeRegistry.SizeOf(guid) > blob->GetNodeDataSize(i))
            {
                throw new InvalidOperationException(
                    $"Node {i} ('{NodeTypeRegistry.TypeOf(guid).FullName}') needs "
                    + $"{NodeTypeRegistry.SizeOf(guid)} bytes but the serialized layout reserves "
                    + $"{blob->GetNodeDataSize(i)} — the layout was built against a different "
                    + "version of the node type.");
            }
        }
    }

    /// <summary>Refuse an array whose self-relative offset or length escapes the loaded bytes.</summary>
    private static void ValidateArrayBounds<T>(
        NodeBlob* blob, int blobLength, ref BlobArray<T> array, string name)
        where T : unmanaged
    {
        if (array.Length < 0)
        {
            throw new InvalidOperationException(
                $"Serialized behavior tree layout is corrupt: array '{name}' has negative length.");
        }

        if (array.Length == 0)
        {
            return;
        }

        long start = (byte*)array.UnsafePtr - (byte*)blob;
        long byteLength = (long)array.Length * sizeof(T);
        if (start < sizeof(NodeBlob) || start + byteLength > blobLength)
        {
            throw new InvalidOperationException(
                $"Serialized behavior tree layout is corrupt: array '{name}' escapes the blob.");
        }
    }

    /// <summary>No finalizer here: the <see cref="NativeBlobAssetReference"/> has its own, so a
    /// layout nobody disposes still gives its memory back.</summary>
    public void Dispose()
    {
        _blob?.Dispose();
        _blob = null;
    }
}

/// <summary>
/// A borrowed view of a <see cref="BehaviorTreeLayout"/> — one pointer at the blob, unmanaged, so
/// it can be stored in an ECS component beside the instance state that points at it.
///
/// It does NOT keep the layout alive. Whoever built the layout owns it; a handle outliving its
/// owner is a dangling pointer, exactly as a physics world handle is.
/// </summary>
public readonly unsafe struct BehaviorTreeLayoutHandle
{
    internal readonly NodeBlob* Blob;

    internal BehaviorTreeLayoutHandle(NodeBlob* blob)
    {
        Blob = blob;
    }

    /// <summary>False for a <c>default</c> handle — which is what an entity reads before anything
    /// published one, since chunk memory is zeroed.</summary>
    public bool IsValid => Blob is not null;

    public int NodeCount => Blob is null ? 0 : Blob->Count;

    public int RuntimeDataSize => Blob is null ? 0 : Blob->RuntimeDataSize;
}

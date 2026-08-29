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
    /// <summary>Node data alignment, so a node holding a vector type is not read across a
    /// boundary. Costs a few bytes per node, once per tree.</summary>
    private const int DataAlignment = 16;

    private NativeBlobAssetReference<NodeBlob>? _blob;

    private BehaviorTreeLayout(NativeBlobAssetReference<NodeBlob> blob) => _blob = blob;

    /// <summary>What an instance points at. Copyable, unmanaged, and valid only while this object
    /// is alive and undisposed.</summary>
    public BehaviorTreeLayoutHandle Handle =>
        new(_blob is null
            ? throw new ObjectDisposedException(nameof(BehaviorTreeLayout))
            : _blob.UnsafePtr);

    /// <summary>How many nodes the tree has — the length of an instance's state buffer.</summary>
    public int NodeCount => Handle.NodeCount;

    /// <summary>How many bytes an instance's runtime node data needs.</summary>
    public int RuntimeDataSize => Handle.RuntimeDataSize;

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

        int size = 0;
        for (int i = 0; i < count; i++)
        {
            IRuntimeNodeFactory factory = tree.GetCompiledNode(i).Factory;
            if (!NodeTypeRegistry.TryGetId(factory.NodeGuid, out int id))
            {
                throw new InvalidOperationException(
                    $"Node type '{factory.NodeType.FullName}' (GUID '{factory.NodeGuid}') "
                    + "is not registered. Node types register themselves through the module "
                    + "initializer the BT generator emits, so this usually means the declaring "
                    + "project does not reference Paradise.BT.Generators as an analyzer, or the "
                    + $"type is not visible to it. Call {nameof(NodeTypeRegistry)}.Register<T>() "
                    + "explicitly for such a node.");
            }

            typeIds[i] = id;
            offsets[i] = size;
            size = Align(size + factory.DataSize);
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
        // is how DefaultData ends up on a 16-byte boundary within the blob. Its per-node offsets
        // are already 16-strided, so a node's data is aligned in the shared block and identically
        // placed in whatever buffer an instance copies it into.
        var builder = new StructBuilder<NodeBlob>();
        builder.SetValue(ref builder.Value.FormatVersion, NodeBlob.CurrentFormatVersion);
        builder.SetArray(ref builder.Value.EndIndices, endIndices, DataAlignment);
        builder.SetArray(ref builder.Value.Types, typeIds, DataAlignment);
        builder.SetArray(ref builder.Value.Offsets, offsets, DataAlignment);
        builder.SetArray(ref builder.Value.DefaultData, defaultData, DataAlignment);

        return new BehaviorTreeLayout(
            builder.CreateNativeBlobAssetReference<NodeBlob>(DataAlignment));
    }

    private static int Align(int value) =>
        (value + (DataAlignment - 1)) & ~(DataAlignment - 1);

    /// <summary>Frees the blob. No finalizer here: <see cref="NativeBlobAssetReference{T}"/> has
    /// its own, so a layout nobody disposes still gives its memory back.</summary>
    public void Dispose()
    {
        _blob?.Dispose();
        _blob = null;
    }
}

/// <summary>
/// A borrowed pointer to a <see cref="BehaviorTreeLayout"/>'s <see cref="NodeBlob"/> — unmanaged,
/// so it can be stored in an ECS component beside the instance state that points at it.
///
/// It does NOT keep the layout alive. Whoever built the layout owns it; a handle outliving its
/// owner is a dangling pointer, exactly as a physics world handle is.
/// </summary>
public readonly unsafe struct BehaviorTreeLayoutHandle
{
    internal readonly NodeBlob* Blob;

    internal BehaviorTreeLayoutHandle(NodeBlob* blob) => Blob = blob;

    /// <summary>False for a <c>default</c> handle — which is what an entity reads before anything
    /// published one, since chunk memory is zeroed.</summary>
    public bool IsValid => Blob is not null;

    public int NodeCount => Blob is null ? 0 : Blob->Count;

    public int RuntimeDataSize => Blob is null ? 0 : Blob->RuntimeDataSize;
}

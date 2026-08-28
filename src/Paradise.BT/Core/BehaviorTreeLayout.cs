using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The shared, immutable half of a compiled tree, as plain memory: per node, where its subtree
/// ends, which type it is, and where its data starts.
///
/// A thousand agents share one layout; each owns only the mutable half (a
/// <see cref="NodeState"/> per node plus a copy of the data), which is small and blittable enough
/// for an ECS component.
///
/// Owns a native allocation that every <see cref="UnmanagedNodeBlob"/> points into: dispose it
/// only once nothing is still ticking against it.
///
/// Node types must be registered with <see cref="NodeTypeRegistry"/> first; an unregistered one is
/// refused by name here rather than faulting on the first tick.
/// </summary>
public sealed unsafe class BehaviorTreeLayout : IDisposable
{
    /// <summary>Node data alignment, so a node holding a vector type is not read across a
    /// boundary. Costs a few bytes per node, once per tree.</summary>
    private const int DataAlignment = 16;

    private LayoutData* _data;

    private BehaviorTreeLayout(LayoutData* data) => _data = data;

    /// <summary>What an instance points at. Copyable, unmanaged, and valid only while this object
    /// is alive and undisposed.</summary>
    public BehaviorTreeLayoutHandle Handle =>
        new(_data is null
            ? throw new ObjectDisposedException(nameof(BehaviorTreeLayout))
            : _data);

    /// <summary>How many nodes the tree has — the length of an instance's state buffer.</summary>
    public int NodeCount => Handle.NodeCount;

    /// <summary>How many bytes an instance's runtime node data needs.</summary>
    public int RuntimeDataSize => Handle.RuntimeDataSize;

    /// <summary>
    /// Flatten a compiled tree into shared memory. Defaults are copied from the tree's own
    /// factories, so there is no second source of them.
    /// </summary>
    /// <exception cref="InvalidOperationException">A node's type was never registered with
    /// <see cref="NodeTypeRegistry"/>, or it holds managed references and cannot be stored as
    /// bytes.</exception>
    public static BehaviorTreeLayout Build(BehaviorTree tree)
    {
        ThrowHelper.ThrowIfNull(tree, nameof(tree));

        int count = tree.Count;
        var typeIds = new int[count];
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

        nuint headerBytes = (nuint)sizeof(LayoutData);
        nuint indexBytes = (nuint)(sizeof(int) * (count + count + count + 1));
        var block = (byte*)NativeMemory.AlignedAlloc(
            headerBytes + indexBytes + (nuint)size, DataAlignment);
        NativeMemory.Clear(block, headerBytes + indexBytes + (nuint)size);

        var data = (LayoutData*)block;
        var cursor = (int*)(block + headerBytes);
        data->NodeCount = count;
        data->RuntimeDataSize = size;
        data->EndIndices = cursor;
        data->TypeIds = cursor + count;
        data->Offsets = cursor + count + count;
        data->DefaultData = block + headerBytes + indexBytes;

        for (int i = 0; i <= count; i++)
        {
            data->Offsets[i] = offsets[i];
        }

        for (int i = 0; i < count; i++)
        {
            BehaviorTreeNode node = tree.GetCompiledNode(i);
            data->EndIndices[i] = node.EndIndex;
            data->TypeIds[i] = typeIds[i];
            node.Factory.WriteDefaultData(
                new Span<byte>(data->DefaultData + offsets[i], node.Factory.DataSize));
        }

        return new BehaviorTreeLayout(data);
    }

    private static int Align(int value) =>
        (value + (DataAlignment - 1)) & ~(DataAlignment - 1);

    public void Dispose()
    {
        if (_data is null)
        {
            return;
        }

        NativeMemory.AlignedFree(_data);
        _data = null;
        GC.SuppressFinalize(this);
    }

    ~BehaviorTreeLayout() => Dispose();
}

/// <summary>
/// A borrowed pointer to a <see cref="BehaviorTreeLayout"/> — unmanaged, so it can be stored in
/// an ECS component beside the instance state that points at it.
///
/// It does NOT keep the layout alive. Whoever built the layout owns it; a handle outliving its
/// owner is a dangling pointer, exactly as a physics world handle is.
/// </summary>
public readonly unsafe struct BehaviorTreeLayoutHandle
{
    internal readonly LayoutData* Data;

    internal BehaviorTreeLayoutHandle(LayoutData* data) => Data = data;

    /// <summary>False for a <c>default</c> handle — which is what an entity reads before anything
    /// published one, since chunk memory is zeroed.</summary>
    public bool IsValid => Data is not null;

    public int NodeCount => Data is null ? 0 : Data->NodeCount;

    public int RuntimeDataSize => Data is null ? 0 : Data->RuntimeDataSize;
}

/// <summary>The layout's header, at the start of its native block; the four arrays follow it.</summary>
internal unsafe struct LayoutData
{
    public int NodeCount;
    public int RuntimeDataSize;
    public int* EndIndices;
    public int* TypeIds;

    /// <summary><c>NodeCount + 1</c> entries, so node <c>i</c>'s data size is
    /// <c>Offsets[i + 1] - Offsets[i]</c> without a second array.</summary>
    public int* Offsets;

    public byte* DefaultData;
}

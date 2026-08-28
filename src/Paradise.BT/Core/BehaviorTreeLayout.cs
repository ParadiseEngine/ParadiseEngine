using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The SHARED, IMMUTABLE half of a compiled behavior tree, as plain memory: per node, where its
/// subtree ends, which type it is, and the bytes its data starts at.
///
/// <b>This is the half that does not vary per instance, and separating it is the point.</b> A
/// thousand agents running one tree share exactly one of these; what each of them owns privately
/// is only the mutable half — a <see cref="NodeState"/> per node and a copy of the node data —
/// which is small, blittable, and therefore something an ECS component can hold. The managed
/// <see cref="NodeBlob"/> makes no such split: it allocates one boxed <c>RuntimeNode</c> per node
/// PER INSTANCE, so a thousand agents means a thousand object graphs.
///
/// <b>Lifetime is the caller's, and it is a real obligation.</b> This owns a native allocation and
/// every <see cref="UnmanagedNodeBlob"/> built from it holds a raw pointer into it. Dispose it
/// only once nothing is still ticking against it — the same contract a physics acceleration
/// structure carries, and for the same reason.
///
/// <b>Every node type must be registered FIRST.</b> A layout resolves each node's <c>[Guid]</c>
/// through <see cref="NodeTypeRegistry"/> and refuses, by name, a type nobody registered — rather
/// than storing an unresolvable id and failing on the first tick, a long way from the cause.
/// </summary>
public sealed unsafe class BehaviorTreeLayout : IDisposable
{
    /// <summary>Node data is aligned to this, so a node containing a vector type is not read
    /// across an alignment boundary. 16 rather than 8 because that is what the widest primitive a
    /// node might reasonably hold wants; the waste is bytes per node, once per tree.</summary>
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
    /// Flatten a compiled tree into shared memory.
    ///
    /// The default data is copied out of the tree's own factories, so a layout says exactly what
    /// <see cref="BehaviorTree.CreateInstance{TBlackboard}"/> would have said — one authored tree,
    /// two runtimes, no second source of defaults.
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
                    $"Node type '{factory.NodeType.FullName}' (GUID '{factory.NodeGuid}') is not "
                    + $"registered. Call {nameof(NodeTypeRegistry)}.Register<T>() for every node "
                    + "type in the tree before building a layout; the built-in set is "
                    + "BuiltInBehaviorNodes.RegisterAll().");
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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.BLOB;

namespace Paradise.BT;

internal readonly struct BehaviorTreeNode
{
    public BehaviorTreeNode(IRuntimeNodeFactory factory, int endIndex)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        EndIndex = endIndex;
    }

    public IRuntimeNodeFactory Factory { get; }

    public int EndIndex { get; }
}

internal interface IRuntimeNodeFactory
{
    int TypeId { get; }

    Type NodeType { get; }

    Guid NodeGuid { get; }

    /// <summary>How many bytes this node's data occupies — what an unmanaged instance reserves
    /// for it. See <see cref="BehaviorTreeLayout"/>.</summary>
    int DataSize { get; }

    /// <summary>
    /// This node's authored default, boxed — one box per node per instance, and the whole of what
    /// <see cref="NodeBlob"/> stores.
    ///
    /// It used to be a <c>RuntimeNode&lt;T&gt;</c>, an object that carried the node's state AND
    /// knew how to tick it AND kept a second copy of the default. The factory is already one per
    /// node type, so the knowing-how-to-tick half lives here now (see <see cref="Tick"/>) and the
    /// default is the factory's single copy; what is per-instance is just the box.
    /// </summary>
    object CreateBoxedData();

    /// <summary>Put a box back to the authored default — the managed <c>CopyDefaultToRuntime</c>,
    /// reading the factory's one copy rather than a per-node duplicate of it.</summary>
    void RestoreDefault(object boxed);

    /// <summary>Tick a node THROUGH its box, so a node writing its own fields persists.</summary>
    NodeState Tick<TNodeBlob, TBlackboard>(
        object boxed, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;

    /// <summary>Reset. Static on <see cref="INodeData"/>, so the box is not needed.</summary>
    void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;

    /// <summary>A ref into the box, typed. Throws if <typeparamref name="T"/> is not this node's
    /// type.</summary>
    ref T DataRef<T>(object boxed) where T : struct;

    /// <summary>A ref to the factory's authored default. Shared by every instance of the tree —
    /// the default IS a property of the tree — so writing through it moves all of them.</summary>
    ref T DefaultRef<T>() where T : struct;

    IBuilder CreateSerializedDefaultDataBuilder();

    /// <summary>
    /// Copy this node's authored default data into <paramref name="destination"/>, which is
    /// exactly <see cref="DataSize"/> bytes.
    ///
    /// The same bytes <see cref="CreateSerializedDefaultDataBuilder"/> would serialize, and
    /// refused on the same grounds — a node holding a managed reference has no byte
    /// representation — but written straight into a caller's buffer rather than through a blob
    /// builder, because a layout is assembling one contiguous block and has nowhere to put an
    /// <c>IBuilder</c>.
    /// </summary>
    void WriteDefaultData(Span<byte> destination);
}

internal sealed class RuntimeNodeFactory<TNodeData> : IRuntimeNodeFactory
    where TNodeData : struct, INodeData
{
    private TNodeData _nodeData;
    private readonly BehaviorNodeMetadata _metadata;

    public RuntimeNodeFactory(TNodeData nodeData, BehaviorNodeMetadata metadata)
    {
        _nodeData = nodeData;
        _metadata = metadata;
    }

    public int TypeId => _metadata.Id;

    public Type NodeType => typeof(TNodeData);

    public Guid NodeGuid => _metadata.Guid;

    public int DataSize => Unsafe.SizeOf<TNodeData>();

    public object CreateBoxedData() => _nodeData;

    public void RestoreDefault(object boxed) => Unsafe.Unbox<TNodeData>(boxed) = _nodeData;

    /// <summary>
    /// <c>Unsafe.Unbox</c> gives a ref INTO the box, so <c>DelayTimerNode.TimerSeconds -=</c>
    /// writes into the stored state and is there next tick. Unboxing to a local instead would
    /// compile, run, and silently restart every timer every frame.
    /// </summary>
    public NodeState Tick<TNodeBlob, TBlackboard>(
        object boxed, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Unsafe.Unbox<TNodeData>(boxed).Tick(index, ref blob, ref bb);

    public void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => TNodeData.Reset(index, ref blob, ref bb);

    public ref T DataRef<T>(object boxed) where T : struct
    {
        ThrowIfNotThisNode<T>();
        return ref Unsafe.As<TNodeData, T>(ref Unsafe.Unbox<TNodeData>(boxed));
    }

    public ref T DefaultRef<T>() where T : struct
    {
        ThrowIfNotThisNode<T>();
        return ref Unsafe.As<TNodeData, T>(ref _nodeData);
    }

    private static void ThrowIfNotThisNode<T>()
    {
        if (typeof(T) != typeof(TNodeData))
        {
            throw new InvalidOperationException(
                $"Node data '{typeof(T).FullName}' does not match '{typeof(TNodeData).FullName}'.");
        }
    }

    public void WriteDefaultData(Span<byte> destination)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot back an unmanaged behavior tree "
                + "instance because it contains managed references.");
        }

        TNodeData nodeData = _nodeData;
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref nodeData, 1))
            .CopyTo(destination);
    }

    public IBuilder CreateSerializedDefaultDataBuilder()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TNodeData>())
        {
            throw new NotSupportedException(
                $"Node '{typeof(TNodeData).FullName}' cannot be serialized with Paradise.BLOB because it contains managed references.");
        }

        TNodeData nodeData = _nodeData;
        var builder = new AnyValueBuilder();
        builder.SetBytes(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref nodeData, 1)),
            GetAlignment<TNodeData>());
        return builder;
    }

    private static int GetAlignment<T>() where T : struct
        => Unsafe.SizeOf<AlignmentHelper<T>>() - Unsafe.SizeOf<T>();

    private struct AlignmentHelper<T> where T : struct
    {
        public byte Padding;
        public T Value;

        public AlignmentHelper(byte padding, T value)
        {
            Padding = padding;
            Value = value;
        }
    }
}

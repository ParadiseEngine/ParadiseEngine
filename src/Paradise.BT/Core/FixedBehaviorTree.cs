using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>Stores a typed tree instance in inline unmanaged buffers suitable for ECS snapshots.</summary>
/// <remarks>Keep the layout alive while instances use its pointer. Tick the actual storage by ref;
/// ticking a copy advances only that copy. Tree types enforce layout and blackboard compatibility.</remarks>
/// <typeparam name="TTree">The tree type, used for compile-time identity without storage.</typeparam>
/// <typeparam name="TStateBuffer">Inline states; size in NodeState units determines node capacity.</typeparam>
/// <typeparam name="TDataBuffer">Inline node data; size in bytes determines data capacity.</typeparam>
public unsafe struct FixedBehaviorTree<TTree, TStateBuffer, TDataBuffer>
    where TStateBuffer : unmanaged
    where TDataBuffer : unmanaged
{
    private BehaviorTreeLayout.LayoutBlob* _layout;
    private TStateBuffer _states;
    private TDataBuffer _data;

    /// <summary>How many nodes fit — a compile-time fact about <typeparamref name="TStateBuffer"/>.</summary>
    public static int MaxNodes => Unsafe.SizeOf<TStateBuffer>() / Unsafe.SizeOf<NodeState>();

    /// <summary>How many bytes of node data fit.</summary>
    public static int MaxDataBytes => Unsafe.SizeOf<TDataBuffer>();

    /// <summary>False for a zeroed instance (chunk memory before anything initialized it).</summary>
    public readonly bool IsInitialized => _layout is not null;

    public NodeState Status => States[0];

    /// <summary>
    /// Point this instance at its tree's layout and copy the authored defaults in. Refuses a tree
    /// bigger than the inline capacity here, where both numbers can be named, rather than as an
    /// overrun into whatever sits next to this struct.
    /// </summary>
    public void Initialize(BehaviorTreeLayout<TTree> layout)
    {
        ArgumentNullException.ThrowIfNull(layout.Untyped);

        ref var blob = ref layout.Untyped.Blob;
        if (blob.Count > MaxNodes || blob.DataSize > MaxDataBytes)
        {
            throw new ArgumentException(
                $"The tree needs {blob.Count} nodes and {blob.DataSize} bytes, "
                + $"but this instance holds at most {MaxNodes} nodes and {MaxDataBytes} bytes. "
                + "Use bigger buffer types — the cost is per instance.", nameof(layout));
        }

        _layout = (BehaviorTreeLayout.LayoutBlob*)Unsafe.AsPointer(ref blob);
        BehaviorTreeRef untyped = UntypedRef;
        untyped.ResetStates(0, blob.Count);
        untyped.ResetRuntimeData(0, blob.Count);
    }

    /// <summary>The typed view over this struct's own bytes, built per use.</summary>
    public BehaviorTreeRef<TTree> Ref => new(UntypedRef);

    /// <summary>Tick, restarting a finished tree first — an instance always loops.</summary>
    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
    {
        BehaviorTreeRef blob = UntypedRef;
        if (blob.GetState(0).IsCompleted())
        {
            VirtualMachine.Reset(blob, blackboard);
        }

        return VirtualMachine.Tick(blob, blackboard);
    }

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
        => VirtualMachine.Reset(UntypedRef, blackboard);

    private BehaviorTreeRef UntypedRef =>
        new(ref Unsafe.AsRef<BehaviorTreeLayout.LayoutBlob>(_layout), States, Data);

    private Span<NodeState> States =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TStateBuffer, NodeState>(ref _states), MaxNodes);

    private Span<byte> Data =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TDataBuffer, byte>(ref _data), MaxDataBytes);
}

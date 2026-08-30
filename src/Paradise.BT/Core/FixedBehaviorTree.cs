using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// An UNMANAGED, TYPED instance: the two per-agent buffers inline, capacity fixed by the buffer
/// type parameters (typically <c>[InlineArray]</c> structs), and the tree's identity carried as a
/// phantom — <see cref="Initialize"/> only accepts a layout compiled from
/// <typeparamref name="TTree"/>, <see cref="Tick{TBlackboard}"/> only that tree's generated
/// blackboard. A plain struct, so it can sit inside an ECS component and ride a snapshot memcpy;
/// the phantom rides the field's type and costs no bytes.
///
/// <b>Lifecycle is the caller's.</b> This holds a raw pointer into the layout's blob, so whoever
/// owns the <see cref="BehaviorTreeLayout"/> must keep it alive and undisposed for as long as any
/// of these ticks against it. Tick through a <c>ref</c> to the real storage — the tree runs over
/// this struct's own bytes, so ticking a copy advances the copy.
/// </summary>
/// <typeparam name="TTree">The tree type this instance runs — the phantom the blackboard is
/// checked against.</typeparam>
/// <typeparam name="TStateBuffer">Inline storage for the per-node states; its size in
/// <see cref="NodeState"/> units is the node capacity.</typeparam>
/// <typeparam name="TDataBuffer">Inline storage for the node data; its size in bytes is the data
/// capacity.</typeparam>
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

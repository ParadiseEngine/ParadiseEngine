using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// An UNMANAGED instance: the two per-agent buffers inline, capacity fixed by the buffer type
/// parameters (typically <c>[InlineArray]</c> structs), so the whole instance is a plain struct —
/// attachable to an entity as (part of) a component, copied whole by a world-snapshot memcpy.
///
/// <b>Lifecycle is the caller's.</b> This holds a raw pointer into the layout's blob, so unlike
/// <see cref="BehaviorTreeInstance"/> it cannot root the layout: whoever owns the
/// <see cref="BehaviorTreeLayout"/> must keep it alive and undisposed for as long as any of these
/// instances ticks against it.
///
/// Tick through a <c>ref</c> to the instance in its real storage — the tree runs over this
/// struct's own bytes, so ticking a copy advances the copy.
/// </summary>
/// <typeparam name="TStateBuffer">Inline storage for the per-node states; its size in
/// <see cref="NodeState"/> units is the node capacity.</typeparam>
/// <typeparam name="TDataBuffer">Inline storage for the node data; its size in bytes is the data
/// capacity.</typeparam>
public unsafe struct FixedBehaviorTreeInstance<TStateBuffer, TDataBuffer>
    where TStateBuffer : unmanaged
    where TDataBuffer : unmanaged
{
    private BehaviorTreeLayout.LayoutBlob* _layout;
    private bool _autoResetOnCompletion;
    private TStateBuffer _states;
    private TDataBuffer _data;

    /// <summary>How many nodes fit — a compile-time fact about <typeparamref name="TStateBuffer"/>.</summary>
    public static int MaxNodes => Unsafe.SizeOf<TStateBuffer>() / Unsafe.SizeOf<NodeState>();

    /// <summary>How many bytes of node data fit.</summary>
    public static int MaxDataBytes => Unsafe.SizeOf<TDataBuffer>();

    /// <summary>False for a zeroed instance (chunk memory before anything initialized it).</summary>
    public readonly bool IsInitialized => _layout is not null;

    /// <summary>Restart a finished tree on its next tick. On by <see cref="Initialize"/>, not by
    /// <c>default</c> — a zeroed struct has it off, like everything else about it.</summary>
    public bool AutoResetOnCompletion
    {
        readonly get => _autoResetOnCompletion;
        set => _autoResetOnCompletion = value;
    }

    public NodeState Status => States[0];

    /// <summary>
    /// Point this instance at a layout and copy the authored defaults in. Refuses a tree bigger
    /// than the inline capacity here, where both numbers can be named, rather than as an overrun
    /// into whatever sits next to this struct.
    /// </summary>
    public void Initialize(BehaviorTreeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        ref var blob = ref layout.Blob;
        if (blob.Count > MaxNodes || blob.DataSize > MaxDataBytes)
        {
            throw new ArgumentException(
                $"The tree needs {blob.Count} nodes and {blob.DataSize} bytes, "
                + $"but this instance holds at most {MaxNodes} nodes and {MaxDataBytes} bytes. "
                + "Use bigger buffer types — the cost is per instance.", nameof(layout));
        }

        _layout = (BehaviorTreeLayout.LayoutBlob*)Unsafe.AsPointer(ref blob);
        _autoResetOnCompletion = true;
        BehaviorTreeRef.Initialize(ref blob, States, Data);
    }

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        BehaviorTreeRef blob = Blob;
        if (_autoResetOnCompletion && blob.GetState(0).IsCompleted())
        {
            VirtualMachine.Reset(blob, blackboard);
        }

        return VirtualMachine.Tick(blob, blackboard);
    }

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
        => VirtualMachine.Reset(Blob, blackboard);

    private BehaviorTreeRef Blob => new(ref Unsafe.AsRef<BehaviorTreeLayout.LayoutBlob>(_layout), States, Data);

    private Span<NodeState> States =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TStateBuffer, NodeState>(ref _states), MaxNodes);

    private Span<byte> Data =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TDataBuffer, byte>(ref _data), MaxDataBytes);
}

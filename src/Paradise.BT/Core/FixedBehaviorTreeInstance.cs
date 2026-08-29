using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// An UNMANAGED instance: the two per-agent buffers inline, capacity fixed by the buffer type
/// parameters (typically <c>[InlineArray]</c> structs), so the whole instance is a plain struct —
/// attachable to an entity as (part of) a component, copied whole by a world-snapshot memcpy.
///
/// <b>Lifecycle is the caller's.</b> An unmanaged struct holds no references, so unlike
/// <see cref="BehaviorTreeInstance"/> this CANNOT root its layout: whoever owns the
/// <see cref="BehaviorTreeLayout"/> must keep it alive and undisposed for as long as any of these
/// instances ticks against it — the same contract as a physics world handle.
/// </summary>
/// <typeparam name="TStateBuffer">Inline storage for the per-node states; its size in
/// <see cref="NodeState"/> units is the node capacity.</typeparam>
/// <typeparam name="TDataBuffer">Inline storage for the node data; its size in bytes is the data
/// capacity.</typeparam>
public struct FixedBehaviorTreeInstance<TStateBuffer, TDataBuffer>
    where TStateBuffer : unmanaged
    where TDataBuffer : unmanaged
{
    private BehaviorTreeLayoutHandle _layout;
    private bool _autoResetOnCompletion;
    private TStateBuffer _states;
    private TDataBuffer _data;

    /// <summary>How many nodes fit — a compile-time fact about <typeparamref name="TStateBuffer"/>.</summary>
    public static int MaxNodes => Unsafe.SizeOf<TStateBuffer>() / Unsafe.SizeOf<NodeState>();

    /// <summary>How many bytes of node data fit.</summary>
    public static int MaxDataBytes => Unsafe.SizeOf<TDataBuffer>();

    /// <summary>False for a zeroed instance (chunk memory before anything initialized it).</summary>
    public readonly bool IsInitialized => _layout.IsValid;

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
    public void Initialize(BehaviorTreeLayoutHandle layout)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException(
                "Behavior tree layout handle is not valid. Build or deserialize a "
                + $"{nameof(BehaviorTreeLayout)} and pass its handle.", nameof(layout));
        }

        if (layout.NodeCount > MaxNodes || layout.RuntimeDataSize > MaxDataBytes)
        {
            throw new ArgumentException(
                $"The tree needs {layout.NodeCount} nodes and {layout.RuntimeDataSize} bytes, "
                + $"but this instance holds at most {MaxNodes} nodes and {MaxDataBytes} bytes. "
                + "Use bigger buffer types — the cost is per instance.", nameof(layout));
        }

        _layout = layout;
        _autoResetOnCompletion = true;
        UnmanagedNodeBlob.Initialize(layout, States, Data);
    }

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        UnmanagedNodeBlob blob = Blob;
        if (_autoResetOnCompletion && blob.GetState(0).IsCompleted())
        {
            VirtualMachine.Reset(blob, blackboard);
        }

        return VirtualMachine.Tick(blob, blackboard);
    }

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard, allows ref struct
        => VirtualMachine.Reset(Blob, blackboard);

    /// <summary>Built per use over this struct's own bytes — which is why every tick must go
    /// through a <c>ref</c> to the instance (a component span, a <c>ref</c> local): ticking a
    /// COPY advances the copy.</summary>
    private UnmanagedNodeBlob Blob => new(_layout, States, Data);

    private Span<NodeState> States =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TStateBuffer, NodeState>(ref _states), MaxNodes);

    private Span<byte> Data =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<TDataBuffer, byte>(ref _data), MaxDataBytes);
}

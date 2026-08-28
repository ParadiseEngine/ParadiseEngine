using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// An <see cref="INodeBlob"/> with no managed state: a borrowed
/// <see cref="BehaviorTreeLayoutHandle"/> for what the tree shares, plus two caller-owned spans
/// for what one instance owns — a <see cref="NodeState"/> per node, and each node's runtime data.
///
/// <see cref="NodeBlob"/> boxes a <c>RuntimeNode&lt;T&gt;</c> per node, which makes an instance an
/// object graph: it cannot live in an ECS component or be memcpy'd into a snapshot. Two blittable
/// buffers can.
///
/// A <c>ref struct</c>, so the compiler checks the spans' lifetime rather than a comment asking
/// the caller to. Two consequences: it cannot be stored in a field, and cannot cross an
/// <c>await</c> (CS4007) — build one where it is used.
///
/// <see cref="GetRuntimeDataPtr"/> still hands out an address, so the runtime span must be
/// non-moveable memory (ECS chunks, <c>NativeMemory</c>; not a <c>byte[]</c>).
/// </summary>
public readonly unsafe ref struct UnmanagedNodeBlob : INodeBlob
{
    private readonly LayoutData* _layout;
    private readonly Span<NodeState> _states;
    private readonly Span<byte> _runtime;
    private readonly int _runtimeId;

    /// <summary>
    /// Point at an instance's memory: <paramref name="states"/> needs
    /// <see cref="BehaviorTreeLayoutHandle.NodeCount"/> entries, <paramref name="runtime"/>
    /// <see cref="BehaviorTreeLayoutHandle.RuntimeDataSize"/> bytes. Does NOT initialise them —
    /// zeroed data reads as a node's zero default rather than its authored one, so a world
    /// builder must call <see cref="Initialize"/>.
    /// </summary>
    public UnmanagedNodeBlob(
        BehaviorTreeLayoutHandle layout, Span<NodeState> states, Span<byte> runtime, int runtimeId = 0)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException(
                "Behavior tree layout handle is not valid. Build a BehaviorTreeLayout and publish "
                + "its Handle before constructing an instance over it.", nameof(layout));
        }

        _layout = layout.Data;
        _states = states;
        _runtime = runtime;
        _runtimeId = runtimeId;
    }

    /// <summary>
    /// Starting state: every node <c>0</c>, every node's data a copy of the authored default.
    /// The counterpart of <c>NodeBlob.Create</c>.
    /// </summary>
    public static void Initialize(
        BehaviorTreeLayoutHandle layout, Span<NodeState> states, Span<byte> runtime)
    {
        if (!layout.IsValid)
        {
            throw new ArgumentException("Behavior tree layout handle is not valid.", nameof(layout));
        }

        LayoutData* data = layout.Data;
        states[..data->NodeCount].Clear();
        new ReadOnlySpan<byte>(data->DefaultData, data->RuntimeDataSize).CopyTo(runtime);
    }

    public int RuntimeId => _runtimeId;

    public int Count => _layout->NodeCount;

    public int GetTypeId(int nodeIndex) => _layout->TypeIds[nodeIndex];

    public int GetEndIndex(int nodeIndex) => _layout->EndIndices[nodeIndex];

    /// <summary>How many bytes <paramref name="count"/> nodes occupy from
    /// <paramref name="startNodeIndex"/> — the RESERVED span, so it includes the padding that
    /// keeps each node's data aligned.</summary>
    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        _layout->Offsets[startNodeIndex + count] - _layout->Offsets[startNodeIndex];

    public NodeState GetState(int nodeIndex) => _states[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => _states[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => _states.Slice(index, count).Clear();

    public IntPtr GetDefaultDataPtr(int nodeIndex) =>
        (IntPtr)(_layout->DefaultData + _layout->Offsets[nodeIndex]);

    /// <summary>The one pointer, taken from the span. Consumed as a <c>ref byte</c> within the
    /// same tick, so it is never held across a move — but the storage must still be
    /// non-moveable.</summary>
    public IntPtr GetRuntimeDataPtr(int nodeIndex) =>
        (IntPtr)Unsafe.AsPointer(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _layout->Offsets[nodeIndex]));

    /// <summary>Scope values are not implemented by any blob in this library — the concept came
    /// across with the EntitiesBT contract and has no consumer here yet.</summary>
    public IntPtr GetDefaultScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope values are not implemented by Paradise.BT.");

    /// <inheritdoc cref="GetDefaultScopeValuePtr"/>
    public IntPtr GetRuntimeScopeValuePtr(int offset)
        => throw new NotSupportedException("Scope values are not implemented by Paradise.BT.");

    // No dispatch method here on purpose: VirtualMachine ticks through GetTypeId +
    // GetRuntimeDataPtr, so its unmanaged path works for ANY byte-backed blob. INodeDataAccessor
    // is not implemented either — reaching an interface member on a ref struct boxes.
}

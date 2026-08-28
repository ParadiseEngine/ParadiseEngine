using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The only <see cref="INodeBlob"/>: a borrowed <see cref="BehaviorTreeLayoutHandle"/> for what
/// the tree shares, plus two caller-owned spans for what one instance owns — a
/// <see cref="NodeState"/> per node, and each node's runtime data.
///
/// Storing node data as bytes rather than as an object per node is what lets an instance live in
/// an ECS component and be memcpy'd into a world snapshot.
///
/// A <c>ref struct</c>, so the compiler checks the spans' lifetime. Two consequences: it cannot be
/// stored in a field, and cannot cross an <c>await</c> (CS4007) — build one where it is used.
///
/// Node data is reached by <c>ref byte</c>, so the spans may be ordinary managed arrays as well as
/// native memory: nothing here takes an address that could outlive a GC move.
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

    /// <summary>Into the LAYOUT's block, which every instance shares and none writes.</summary>
    public ref byte DefaultData(int nodeIndex) =>
        ref Unsafe.AsRef<byte>(_layout->DefaultData + _layout->Offsets[nodeIndex]);

    /// <summary>Into this instance's own span. A ref rather than an address, so the caller may
    /// back it with a managed array.</summary>
    public ref byte RuntimeData(int nodeIndex) =>
        ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _layout->Offsets[nodeIndex]);

    // No dispatch method here on purpose: VirtualMachine ticks through GetTypeId + RuntimeData,
    // both INodeBlob members, so it works for any byte-backed blob rather than this type alone.
}

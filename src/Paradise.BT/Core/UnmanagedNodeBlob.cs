using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The only <see cref="INodeBlob"/>: a borrowed <see cref="BehaviorTreeLayoutHandle"/> for the
/// shared <see cref="NodeBlob"/>, plus two caller-owned spans for what one instance owns — a
/// <see cref="NodeState"/> per node, and each node's runtime data.
///
/// This is EntitiesBT's <c>NodeBlobRef</c> with the instance state pulled out of the blob: storing
/// node data as bytes rather than as an object per node is what lets an instance live in an ECS
/// component and be memcpy'd into a world snapshot, and keeping it OUT of the blob is what lets a
/// thousand agents share one.
///
/// A <c>ref struct</c>, so the compiler checks the spans' lifetime. Two consequences: it cannot be
/// stored in a field, and cannot cross an <c>await</c> (CS4007) — build one where it is used.
///
/// Node data is reached by <c>ref byte</c>, so the spans may be ordinary managed arrays as well as
/// native memory: nothing here takes an address that could outlive a GC move.
/// </summary>
public readonly unsafe ref struct UnmanagedNodeBlob : INodeBlob
{
    private readonly NodeBlob* _blob;
    private readonly int* _registryIds;
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

        // The one unguarded entry: RuntimeData reaches past span bounds checks on purpose
        // (Unsafe.Add over an offset), so an undersized buffer here is a SILENT write into
        // whatever sits next to it — for chunk memory, another entity's components.
        if (states.Length < layout.NodeCount)
        {
            throw new ArgumentException(
                $"The states span holds {states.Length} entries but the layout has "
                + $"{layout.NodeCount} nodes.", nameof(states));
        }

        if (runtime.Length < layout.RuntimeDataSize)
        {
            throw new ArgumentException(
                $"The runtime span holds {runtime.Length} bytes but the layout's node data "
                + $"needs {layout.RuntimeDataSize}.", nameof(runtime));
        }

        _blob = layout.Blob;
        _registryIds = layout.RegistryIds;
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

        NodeBlob* blob = layout.Blob;
        states[..blob->Count].Clear();
        blob->DefaultData.ToSpan().CopyTo(runtime);
    }

    public int RuntimeId => _runtimeId;

    public int Count => _blob->Count;

    /// <summary>The registry id dispatch needs: the node's GUID-table index, resolved through the
    /// process-local table the handle carries — the blob itself knows only GUIDs.</summary>
    public int GetTypeId(int nodeIndex) => _registryIds[_blob->Types[nodeIndex]];

    public int GetEndIndex(int nodeIndex) => _blob->EndIndices[nodeIndex];

    /// <inheritdoc cref="NodeBlob.GetNodeDataSize"/>
    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        _blob->GetNodeDataSize(startNodeIndex, count);

    public NodeState GetState(int nodeIndex) => _states[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => _states[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => _states.Slice(index, count).Clear();

    /// <summary>Into the BLOB's defaults, which every instance shares and none writes. Reached
    /// through the pointer, never through a copy of the blob — see <see cref="NodeBlob"/>.</summary>
    public ref byte DefaultData(int nodeIndex) =>
        ref _blob->DefaultData[_blob->Offsets[nodeIndex]];

    /// <summary>Into this instance's own span. A ref rather than an address, so the caller may
    /// back it with a managed array.</summary>
    public ref byte RuntimeData(int nodeIndex) =>
        ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _blob->Offsets[nodeIndex]);

    // No dispatch method here on purpose: VirtualMachine ticks through GetTypeId + RuntimeData,
    // both INodeBlob members, so it works for any byte-backed blob rather than this type alone.
}

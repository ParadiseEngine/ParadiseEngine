using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>
/// The only <see cref="IBehaviorTree"/>: a borrowed <see cref="BehaviorTreeLayout.LayoutBlob"/> for the
/// shared layout, plus two caller-owned spans for what one instance owns — a
/// <see cref="NodeState"/> per node, and each node's runtime data. Instance state as plain bytes
/// is what lets an instance live in an ECS component and ride a snapshot memcpy.
///
/// A <c>ref struct</c>, so the compiler checks the spans' lifetime: build one where it is used —
/// it cannot be a field or cross an <c>await</c> (CS4007).
/// </summary>
public readonly ref struct BehaviorTreeRef : IBehaviorTree
{
    private readonly ref BehaviorTreeLayout.LayoutBlob _blob;
    private readonly Span<NodeState> _states;
    private readonly Span<byte> _runtime;

    public BehaviorTreeRef(BehaviorTreeLayout layout, Span<NodeState> states, Span<byte> runtime)
    {
        // The one unguarded entry: RuntimeData reaches past span bounds checks on purpose
        // (Unsafe.Add over an offset), so an undersized buffer here is a SILENT write into
        // whatever sits next to it — for chunk memory, another entity's components.
        if (states.Length < layout.Blob.Count)
        {
            throw new ArgumentException(
                $"The states span holds {states.Length} entries but the layout has "
                + $"{layout.Blob.Count} nodes.", nameof(states));
        }

        if (runtime.Length < layout.Blob.DataSize)
        {
            throw new ArgumentException(
                $"The runtime span holds {runtime.Length} bytes but the layout's node data "
                + $"needs {layout.Blob.DataSize}.", nameof(runtime));
        }

        _blob = ref layout.Blob;
        _states = states;
        _runtime = runtime;
    }

    public static void Initialize(BehaviorTreeLayout layout, Span<NodeState> states, Span<byte> runtime)
    {
        ref var blob = ref layout.Blob;
        states[..blob.Count].Clear();
        blob.DefaultData.ToSpan().CopyTo(runtime);
    }

    public Guid GetTypeGuid(int nodeIndex) => _blob.TypeGuid(nodeIndex);

    public int GetEndIndex(int nodeIndex) => _blob.EndIndices[nodeIndex];

    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        _blob.GetNodeDataSize(startNodeIndex, count);

    public NodeState GetState(int nodeIndex) => _states[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => _states[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => _states.Slice(index, count).Clear();

    public ref byte DefaultData(int nodeIndex) =>
        ref _blob.DefaultData[_blob.Offsets[nodeIndex]];

    public ref byte RuntimeData(int nodeIndex) =>
        ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _blob.Offsets[nodeIndex]);
}

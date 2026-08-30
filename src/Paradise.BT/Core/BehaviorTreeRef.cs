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
    private readonly ref BehaviorTreeLayout.LayoutBlob _layout;
    private readonly Span<NodeState> _states;
    private readonly Span<byte> _runtime;

    public BehaviorTreeRef(ref BehaviorTreeLayout.LayoutBlob layout, Span<NodeState> states, Span<byte> runtime)
    {
        // The one unguarded entry: RuntimeData reaches past span bounds checks on purpose
        // (Unsafe.Add over an offset), so an undersized buffer here is a SILENT write into
        // whatever sits next to it — for chunk memory, another entity's components.
        if (states.Length < layout.Count)
        {
            throw new ArgumentException(
                $"The states span holds {states.Length} entries but the layout has "
                + $"{layout.Count} nodes.", nameof(states));
        }

        if (runtime.Length < layout.DataSize)
        {
            throw new ArgumentException(
                $"The runtime span holds {runtime.Length} bytes but the layout's node data "
                + $"needs {layout.DataSize}.", nameof(runtime));
        }

        _layout = ref layout;
        _states = states;
        _runtime = runtime;
    }

    public Guid GetTypeGuid(int nodeIndex) => _layout.TypeGuid(nodeIndex);

    public int GetEndIndex(int nodeIndex) => _layout.EndIndices[nodeIndex];

    public int GetNodeDataSize(int startNodeIndex, int count = 1) =>
        _layout.GetNodeDataSize(startNodeIndex, count);

    public NodeState GetState(int nodeIndex) => _states[nodeIndex];

    public void SetState(int nodeIndex, NodeState state) => _states[nodeIndex] = state;

    public void ResetStates(int index, int count = 1) => _states.Slice(index, count).Clear();

    public ref byte DefaultData(int nodeIndex) =>
        ref _layout.DefaultData[_layout.Offsets[nodeIndex]];

    public ref byte RuntimeData(int nodeIndex) =>
        ref Unsafe.Add(ref MemoryMarshal.GetReference(_runtime), _layout.Offsets[nodeIndex]);
}

/// <summary>
/// <see cref="BehaviorTreeRef"/> plus the tree's identity: Tick and Reset only accept a
/// blackboard the binding generator stamped for the same <typeparamref name="TTree"/>.
/// </summary>
public readonly ref struct BehaviorTreeRef<TTree>(BehaviorTreeRef untyped)
{
    public BehaviorTreeRef Untyped { get; } = untyped;

    public NodeState Status => Untyped.GetState(0);

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
        => VirtualMachine.Tick(Untyped, blackboard);

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
        => VirtualMachine.Reset(Untyped, blackboard);
}

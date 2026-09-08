using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT;

/// <summary>Views a borrowed layout and caller-owned spans of node states and runtime bytes.</summary>
/// <remarks>The state can live in ECS components and memcpy snapshots. This ref struct must
/// remain within the lifetime of its spans and cannot cross await.</remarks>
public readonly ref struct BehaviorTreeRef : IBehaviorTree
{
    private readonly ref BehaviorTreeLayout.LayoutBlob _layout;
    private readonly Span<NodeState> _states;
    private readonly Span<byte> _runtime;

    public BehaviorTreeRef(ref BehaviorTreeLayout.LayoutBlob layout, Span<NodeState> states, Span<byte> runtime)
    {
        // RuntimeData uses unchecked offsets; validate buffer sizes here to prevent adjacent-memory writes.
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
public readonly ref struct BehaviorTreeRef<TTree>
{
    internal BehaviorTreeRef(BehaviorTreeRef untyped) => Untyped = untyped;

    public BehaviorTreeRef Untyped { get; }

    public NodeState Status => Untyped.GetState(0);

    public NodeState Tick<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
        => VirtualMachine.Tick(Untyped, blackboard);

    public void Reset<TBlackboard>(TBlackboard blackboard)
        where TBlackboard : struct, IBlackboardFor<TTree>, allows ref struct
        => VirtualMachine.Reset(Untyped, blackboard);
}

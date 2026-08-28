using System.Runtime.InteropServices;

namespace Paradise.BT.Test;

/// <summary>
/// The unmanaged blob must be a drop-in for the managed one: same trees, same nodes, same states,
/// tick for tick. These run both side by side rather than asserting expected values, because the
/// claim being made is EQUIVALENCE — a test that hardcoded the answer would still pass if both
/// implementations drifted the same way.
///
/// The three claims beyond parity are the ones the design rests on: node data mutated in place
/// really does persist (a timer counts down), two instances over one layout do not share state,
/// and an instance survives being memcpy'd — which is what "a behavior tree can live in an ECS
/// component and ride a world snapshot" actually means.
/// </summary>
public sealed class UnmanagedBlobTests
{
    /// <summary>An instance's two buffers, allocated natively so the pointers the blob keeps stay
    /// put. A caller in a game hands it ECS chunk memory instead; the shape is the same.</summary>
    private sealed unsafe class Instance : IDisposable
    {
        private readonly BehaviorTreeLayoutHandle _layout;
        private readonly int _nodeCount;
        private readonly int _runtimeSize;
        private NodeState* _states;
        private byte* _runtime;

        public Instance(BehaviorTreeLayout layout)
        {
            _layout = layout.Handle;
            _nodeCount = layout.NodeCount;
            _runtimeSize = Math.Max(1, layout.RuntimeDataSize);
            _states = (NodeState*)NativeMemory.Alloc((nuint)(sizeof(NodeState) * _nodeCount));
            _runtime = (byte*)NativeMemory.Alloc((nuint)_runtimeSize);
            UnmanagedNodeBlob.Initialize(_layout, States, Runtime);
        }

        /// <summary>The buffers as spans. Native memory, so the blob may take an address into
        /// them — the contract GetRuntimeDataPtr states.</summary>
        private Span<NodeState> States => new(_states, _nodeCount);

        private Span<byte> Runtime => new(_runtime, _runtimeSize);

        /// <summary>
        /// The blob is built fresh at each use rather than held in a field: it is a ref struct
        /// now, so it cannot be stored in a class, and — the thing that actually bites in a test
        /// suite — it cannot live across an <c>await</c> (CS4007). Building it is two span
        /// constructions.
        /// </summary>
        public UnmanagedNodeBlob Blob => new(_layout, States, Runtime);

        public NodeState Status => _states[0];

        /// <summary>Topology, copied OUT, so an assertion can await without the blob being live.</summary>
        public (int Count, int[] EndIndices) Topology()
        {
            UnmanagedNodeBlob blob = Blob;
            var ends = new int[blob.Count];
            for (var i = 0; i < ends.Length; i++)
            {
                ends[i] = blob.GetEndIndex(i);
            }
            return (blob.Count, ends);
        }

        /// <summary>Reset, as a method, for the same reason: it keeps the blob off the caller's
        /// stack frame and therefore out of its async state machine.</summary>
        public void Reset(ref Blackboard bb)
        {
            UnmanagedNodeBlob blob = Blob;
            VirtualMachine.Reset(ref blob, ref bb);
        }

        /// <summary>Mirrors <see cref="BehaviorTreeInstance{T}.Tick"/> — including its
        /// auto-reset-on-completion, so a comparison against the managed instance is comparing
        /// the same policy and not two different ones.</summary>
        public NodeState Tick(ref Blackboard bb, float deltaTime)
        {
            bb.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
            UnmanagedNodeBlob blob = Blob;
            if (Status.IsCompleted())
            {
                VirtualMachine.Reset(ref blob, ref bb);
            }

            return VirtualMachine.Tick(ref blob, ref bb);
        }

        /// <summary>Copy this instance's whole state over another's — a world snapshot, in
        /// miniature.</summary>
        public void CopyTo(Instance other, BehaviorTreeLayout layout)
        {
            new Span<byte>(_states, sizeof(NodeState) * layout.NodeCount)
                .CopyTo(new Span<byte>(other._states, sizeof(NodeState) * layout.NodeCount));
            new Span<byte>(_runtime, layout.RuntimeDataSize)
                .CopyTo(new Span<byte>(other._runtime, layout.RuntimeDataSize));
        }

        public void Dispose()
        {
            if (_states is not null)
            {
                NativeMemory.Free(_states);
                _states = null;
            }

            if (_runtime is not null)
            {
                NativeMemory.Free(_runtime);
                _runtime = null;
            }
        }
    }

    private static BehaviorNodeDefinition SampleTree() =>
        BuiltInBehaviorNodes.Selector(
            BuiltInBehaviorNodes.Sequence(
                BuiltInBehaviorNodes.Success(),
                BuiltInBehaviorNodes.Delay(0.5f),
                BuiltInBehaviorNodes.Success()),
            BuiltInBehaviorNodes.Inverter(BuiltInBehaviorNodes.Failure()));

    private static BehaviorTreeLayout LayoutOf(BehaviorNodeDefinition definition)
    {
        BuiltInBehaviorNodes.RegisterAll();
        return BehaviorTreeLayout.Build(BehaviorTreeBuilder.Build(definition));
    }

    [Test]
    public async Task Unmanaged_Ticks_Identically_To_Managed()
    {
        BehaviorTree managedTree = BehaviorTreeBuilder.Build(SampleTree());
        BehaviorTreeInstance<Blackboard> managed = managedTree.CreateInstance(new Blackboard());

        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        using var unmanaged = new Instance(layout);
        var bb = new Blackboard();

        for (int i = 0; i < 20; i++)
        {
            NodeState expected = managed.Tick(0.2f);
            NodeState actual = unmanaged.Tick(ref bb, 0.2f);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Unmanaged_Reports_The_Same_Topology()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(SampleTree());
        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        using var instance = new Instance(layout);
        (int count, int[] ends) = instance.Topology();

        await Assert.That(count).IsEqualTo(tree.Count);
        for (int i = 0; i < tree.Count; i++)
        {
            await Assert.That(ends[i]).IsEqualTo(tree.GetEndIndex(i));
        }
    }

    /// <summary>
    /// The claim <see cref="NodeInvoker{T}"/>'s doc rests on: a node ticks THROUGH its bytes, so
    /// <c>TimerSeconds -=</c> lands in the instance and the timer actually counts down. Ticking a
    /// copy instead would leave the delay permanently Running.
    /// </summary>
    [Test]
    public async Task Node_Data_Mutated_During_A_Tick_Persists()
    {
        // 0.25 rather than 0.30 so the third step lands clearly PAST zero. Three 0.1f steps out of
        // 0.3f leave about 1e-8 behind rather than nothing, which would make this test a check on
        // float residue instead of on whether the subtraction was written back at all.
        using BehaviorTreeLayout layout = LayoutOf(BuiltInBehaviorNodes.Delay(0.25f));
        using var instance = new Instance(layout);
        var bb = new Blackboard();

        await Assert.That(instance.Tick(ref bb, 0.1f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(ref bb, 0.1f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(ref bb, 0.1f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Reset_Restores_The_Authored_Default()
    {
        using BehaviorTreeLayout layout = LayoutOf(BuiltInBehaviorNodes.Delay(0.3f));
        using var instance = new Instance(layout);
        var bb = new Blackboard();

        instance.Tick(ref bb, 0.25f);
        instance.Reset(ref bb);

        // Back to 0.3s, so 0.25 is once again not enough to complete it.
        await Assert.That(instance.Tick(ref bb, 0.25f)).IsEqualTo(NodeState.Running);
    }

    /// <summary>
    /// One layout, many agents. If the shared half leaked any mutable state, ticking one instance
    /// would advance the other's timer — which is precisely the bug that makes a shared tree
    /// unusable for a crowd.
    /// </summary>
    [Test]
    public async Task Two_Instances_Over_One_Layout_Do_Not_Share_State()
    {
        using BehaviorTreeLayout layout = LayoutOf(BuiltInBehaviorNodes.Delay(0.3f));
        using var first = new Instance(layout);
        using var second = new Instance(layout);
        var bb = new Blackboard();

        first.Tick(ref bb, 0.25f);
        first.Tick(ref bb, 0.25f);

        // The second has never been ticked, so its timer is untouched and one small step is not
        // enough to finish it.
        await Assert.That(second.Tick(ref bb, 0.1f)).IsEqualTo(NodeState.Running);
    }

    /// <summary>
    /// The whole point of the exercise: an instance is BYTES, so copying those bytes copies the
    /// behavior tree mid-flight. This is what lets one live in an ECS component that a world
    /// snapshot memcpy's.
    /// </summary>
    [Test]
    public async Task An_Instance_Survives_A_Raw_Memory_Copy()
    {
        using BehaviorTreeLayout layout = LayoutOf(BuiltInBehaviorNodes.Delay(0.3f));
        using var original = new Instance(layout);
        using var copy = new Instance(layout);
        var bb = new Blackboard();

        original.Tick(ref bb, 0.25f);
        original.CopyTo(copy, layout);

        // The copy resumes exactly where the original was: 0.05s left, so one more step finishes.
        await Assert.That(copy.Tick(ref bb, 0.1f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Layout_Refuses_A_Node_Type_Nobody_Registered()
    {
        BehaviorTree tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new UnregisteredNode()));

        await Assert.That(() => BehaviorTreeLayout.Build(tree))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(UnregisteredNode));
    }

    [Test]
    public async Task Registering_The_Same_Type_Twice_Returns_One_Id()
    {
        int first = NodeTypeRegistry.Register<SequenceNode>();
        int second = NodeTypeRegistry.Register<SequenceNode>();

        await Assert.That(second).IsEqualTo(first);
    }

    /// <summary>A node type this test file registers nowhere, so the layout has to refuse it.</summary>
    [System.Runtime.InteropServices.Guid("6D6F4F4F-2C4F-4E8B-9F1D-5B2A7C3E0A11")]
    private struct UnregisteredNode : INodeData
    {
        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob, allows ref struct
            where TBlackboard : struct, IBlackboard
            => NodeState.Success;
    }
}

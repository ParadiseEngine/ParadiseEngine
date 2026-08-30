using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
using System.Runtime.InteropServices;

namespace Paradise.BT.Test;

/// <summary>
/// The unmanaged blob must be a drop-in for the managed one. These run both side by side rather
/// than asserting expected values: the claim is EQUIVALENCE, and a hardcoded answer would still
/// pass if both drifted the same way.
///
/// Three further claims the design rests on: node data mutated in place persists, two instances
/// over one layout do not share state, and an instance survives a memcpy — which is what "can ride
/// a world snapshot" means.
/// </summary>
public sealed class UnmanagedBlobTests
{
    /// <summary>An instance's two buffers, allocated natively so the pointers the blob keeps stay
    /// put. A caller in a game hands it ECS chunk memory instead; the shape is the same.</summary>
    private sealed unsafe class Instance : IDisposable
    {
        private readonly BehaviorTreeLayout _layout;
        private readonly int _nodeCount;
        private readonly int _runtimeSize;
        private NodeState* _states;
        private byte* _runtime;

        public Instance(BehaviorTreeLayout layout)
        {
            _layout = layout;
            _nodeCount = layout.Blob.Count;
            _runtimeSize = Math.Max(1, layout.Blob.DataSize);
            _states = (NodeState*)NativeMemory.Alloc((nuint)(sizeof(NodeState) * _nodeCount));
            _runtime = (byte*)NativeMemory.Alloc((nuint)_runtimeSize);
            BehaviorTree.Initialize(_layout, States, Runtime);
        }

        /// <summary>Native memory, so the blob may take an address into it.</summary>
        private Span<NodeState> States => new(_states, _nodeCount);

        private Span<byte> Runtime => new(_runtime, _runtimeSize);

        /// <summary>Built fresh at each use: a ref struct cannot be stored in a class, nor live
        /// across an <c>await</c> (CS4007).</summary>
        public BehaviorTree Blob => new(_layout, States, Runtime);

        public NodeState Status => _states[0];

        /// <summary>Copied OUT, so an assertion can await without the blob being live.</summary>
        public (int Count, int[] EndIndices) Topology()
        {
            BehaviorTree blob = Blob;
            var ends = new int[_nodeCount];
            for (var i = 0; i < ends.Length; i++)
            {
                ends[i] = blob.GetEndIndex(i);
            }
            return (_nodeCount, ends);
        }

        /// <summary>A method for the same reason: keeps the blob out of the caller's async state
        /// machine.</summary>
        public void Reset(ref Blackboard bb)
        {
            BehaviorTree blob = Blob;
            VirtualMachine.Reset(blob, bb);
        }

        /// <summary>Mirrors <see cref="BehaviorTreeInstance.Tick{T}"/>, auto-reset included, so the
        /// comparison is against the same policy.</summary>
        public NodeState Tick(ref Blackboard bb)
        {
            BehaviorTree blob = Blob;
            if (Status.IsCompleted())
            {
                VirtualMachine.Reset(blob, bb);
            }

            return VirtualMachine.Tick(blob, bb);
        }

        /// <summary>Copy this instance's whole state over another's — a world snapshot, in
        /// miniature.</summary>
        public void CopyTo(Instance other, BehaviorTreeLayout layout)
        {
            new Span<byte>(_states, sizeof(NodeState) * layout.Blob.Count)
                .CopyTo(new Span<byte>(other._states, sizeof(NodeState) * layout.Blob.Count));
            new Span<byte>(_runtime, layout.Blob.DataSize)
                .CopyTo(new Span<byte>(other._runtime, layout.Blob.DataSize));
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

    private static BTreeNode SampleTree() =>
        new Selector(
            new Sequence(
                new Success(),
                new Repeat(2, new Success()),
                new Success()),
            new Inverter(new Failure()));

    /// <summary>No registration call: the built-in node types register themselves through a
    /// generated module initializer.</summary>
    private static BehaviorTreeLayout LayoutOf(BTreeNode definition) =>
        BTreeNode.Build(definition);

    [Test]
    public async Task Unmanaged_Ticks_Identically_To_Managed()
    {
        using BehaviorTreeLayout managedLayout = BTreeNode.Build(SampleTree());
        TestInstance<Blackboard> managed = managedLayout.CreateInstance(new Blackboard());

        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        using var unmanaged = new Instance(layout);
        var bb = new Blackboard();

        for (int i = 0; i < 20; i++)
        {
            NodeState expected = managed.Tick();
            NodeState actual = unmanaged.Tick(ref bb);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Unmanaged_Reports_The_Same_Topology()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(SampleTree());
        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        using var instance = new Instance(layout);
        (int count, int[] ends) = instance.Topology();

        await Assert.That(count).IsEqualTo(tree.Blob.Count);
        for (int i = 0; i < tree.Blob.Count; i++)
        {
            await Assert.That(ends[i]).IsEqualTo(tree.GetEndIndex(i));
        }
    }

    /// <summary>A node ticks THROUGH its bytes, so <c>TickTimes--</c> lands in the instance.
    /// Ticking a copy would leave the repeat permanently Running.</summary>
    [Test]
    public async Task Node_Data_Mutated_During_A_Tick_Persists()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Repeat(3, new Success()));
        using var instance = new Instance(layout);
        var bb = new Blackboard();

        await Assert.That(instance.Tick(ref bb)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(ref bb)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(ref bb)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Reset_Restores_The_Authored_Default()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Repeat(2, new Success()));
        using var instance = new Instance(layout);
        var bb = new Blackboard();

        instance.Tick(ref bb);
        instance.Reset(ref bb);

        // Back to 2 repetitions, so one tick is once again not enough to complete it.
        await Assert.That(instance.Tick(ref bb)).IsEqualTo(NodeState.Running);
    }

    /// <summary>One layout, many agents. If the shared half leaked mutable state, ticking one
    /// instance would advance the other's counter.</summary>
    [Test]
    public async Task Two_Instances_Over_One_Layout_Do_Not_Share_State()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Repeat(2, new Success()));
        using var first = new Instance(layout);
        using var second = new Instance(layout);
        var bb = new Blackboard();

        first.Tick(ref bb);
        first.Tick(ref bb);

        // The second has never been ticked, so its counter is untouched and one tick is not
        // enough to finish it.
        await Assert.That(second.Tick(ref bb)).IsEqualTo(NodeState.Running);
    }

    /// <summary>An instance is BYTES, so copying them copies the tree mid-flight — what lets one
    /// live in a component a snapshot memcpy's.</summary>
    [Test]
    public async Task An_Instance_Survives_A_Raw_Memory_Copy()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Repeat(2, new Success()));
        using var original = new Instance(layout);
        using var copy = new Instance(layout);
        var bb = new Blackboard();

        original.Tick(ref bb);
        original.CopyTo(copy, layout);

        // The copy resumes exactly where the original was: one repetition left, so one more tick
        // finishes.
        await Assert.That(copy.Tick(ref bb)).IsEqualTo(NodeState.Success);
    }

    /// <summary>RuntimeData reaches past span bounds checks on purpose, so an undersized buffer
    /// would be a SILENT write into neighbouring memory — the constructor is the one place to
    /// refuse it.</summary>
    [Test]
    public async Task Blob_Refuses_Undersized_Buffers()
    {
        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        int nodeCount = layout.Blob.Count;
        int dataSize = layout.Blob.DataSize;

        await Assert.That(() =>
        {
            Span<NodeState> shortStates = stackalloc NodeState[nodeCount - 1];
            Span<byte> runtime = stackalloc byte[dataSize];
            _ = new BehaviorTree(layout, shortStates, runtime);
        }).Throws<ArgumentException>().WithMessageContaining("states");

        await Assert.That(() =>
        {
            Span<NodeState> states = stackalloc NodeState[nodeCount];
            Span<byte> shortRuntime = stackalloc byte[dataSize - 1];
            _ = new BehaviorTree(layout, states, shortRuntime);
        }).Throws<ArgumentException>().WithMessageContaining("runtime");
    }

    [Test]
    public async Task Build_Refuses_A_Node_Type_Nobody_Registered()
    {
        await Assert.That(() => new LeafNode<UnregisteredNode>(new UnregisteredNode()).Build())
            .Throws<InvalidOperationException>()
            .WithMessageContaining(nameof(UnregisteredNode));
    }


    /// <summary>Registered nowhere, so the layout must refuse it.</summary>
    [System.Runtime.InteropServices.Guid("6D6F4F4F-2C4F-4E8B-9F1D-5B2A7C3E0A11")]
    private struct UnregisteredNode : INode
    {
        public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
            => NodeState.Success;
    }
}

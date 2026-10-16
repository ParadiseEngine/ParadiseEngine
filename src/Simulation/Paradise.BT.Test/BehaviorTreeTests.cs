using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class BehaviorTreeTests
{
    private struct ResetCallData
    {
        public int Value;
    }

    private struct PreTickData
    {
        public int Value;
    }

    [System.Runtime.InteropServices.Guid("F4E3D2C1-B0A9-4867-8765-432109FEDCBA")]
    [Reads<PreTickData>]
    internal struct ReadBlackboardNode : INode
    {
        public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            var data = bb.GetData<PreTickData>();
            return data.Value == 42 ? NodeState.Success : NodeState.Failure;
        }
    }

    [System.Runtime.InteropServices.Guid("A1523157-2737-48A0-8F1D-14D07B5F4D77")]
    internal struct CountingNode : INode
    {
        public int Count;

        public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            Count++;
            return Count >= 2 ? NodeState.Success : NodeState.Running;
        }
    }

    [System.Runtime.InteropServices.Guid("324C79B0-5CAB-4953-9A3F-9490C6361AE5")]
    [Writes<ResetCallData>]
    internal struct ResetAwareNode : INode
    {
        public int Count;

        public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            Count++;
            return Count >= 2 ? NodeState.Success : NodeState.Running;
        }

        public static void Reset<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            var resetCall = bb.GetData<ResetCallData>();
            bb.SetData(resetCall with { Value = resetCall.Value + 1 });
        }
    }

    [Test]
    public async Task Completed_Root_Auto_Resets_On_Next_Tick()
    {
        using var tree = BTreeNode.Build(
            TestBehaviorNodes.Probe());

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount()).IsEqualTo(2);
    }

    [Test]
    public async Task Selector_Stops_After_First_Success()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                TestBehaviorNodes.Probe(slot: 0),
                TestBehaviorNodes.Probe(slot: 1)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        NodeState status = instance.Tick();

        await Assert.That(status).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount(0)).IsEqualTo(1);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(0);
    }

    [Test]
    public async Task Repeat_Completes_After_Configured_Number_Of_Successes()
    {
        using var tree = BTreeNode.Build(
            new Repeat(
                3,
                TestBehaviorNodes.Probe()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount()).IsEqualTo(3);
    }

    [Test]
    public async Task Repeat_With_MultiTick_Child_Completes_Correct_Number_Of_Times()
    {
        using var tree = BTreeNode.Build(
            new Repeat(
                3,
                TestBehaviorNodes.ProbeAlternating(
                    odd: NodeState.Running, even: NodeState.Success)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // Repeats count completions, so three two-tick children require six ticks.
        for (int tick = 0; tick < 5; tick++)
        {
            await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        }

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        await Assert.That(instance.ProbeCount()).IsEqualTo(6);
    }

    [Test]
    public async Task Parallel_Returns_Failure_When_Any_Child_Fails_And_None_Are_Running()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Preserves_Completed_Children_State()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new LeafNode<CountingNode>(new CountingNode())));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Parallel_All_Children_Already_Completed_Returns_Valid_State()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);

        // No child runs again, but their saved states must still contribute to the result.
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Preserves_Failed_Child_State_Alongside_Running_Child()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Failure(),
                new LeafNode<CountingNode>(new CountingNode())));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // Running takes priority until the remaining child finishes.
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Custom_Struct_Node_Can_Be_Authored_Through_Interface_Constraints()
    {
        using var tree = BTreeNode.Build(new LeafNode<CountingNode>(new CountingNode()));
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Returns_Failure_Not_Zero_When_Child_Already_Failed()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                new Failure(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_Returns_Success_Not_Zero_When_Child_Already_Succeeded()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                new Success(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Returns_Success_On_Retick_When_All_Children_Already_Succeeded()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Custom_Reset_Action_Runs_When_Tree_Resets()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new ResetCallData());

        using var tree = BTreeNode.Build(new LeafNode<ResetAwareNode>(new ResetAwareNode()));
        TestInstance<Blackboard> instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(1);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(1);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Blackboard.GetData<ResetCallData>().Value).IsEqualTo(2);
    }

    [Test]
    public async Task Blackboard_Mutations_Before_First_Tick_Are_Preserved()
    {
        using var tree = BTreeNode.Build(new LeafNode<ReadBlackboardNode>(new ReadBlackboardNode()));
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        instance.Blackboard.SetData(new PreTickData { Value = 42 });

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

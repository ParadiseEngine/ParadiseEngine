using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class NodeTests
{
    // SequenceNode

    [Test]
    public async Task Sequence_All_Children_Succeed_Returns_Success()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Success(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_First_Child_Fails_Returns_Failure_Without_Ticking_Rest()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                new Failure(),
                TestBehaviorNodes.Probe(slot: 1)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(0);
    }

    [Test]
    public async Task Sequence_Running_Child_Resumes_On_Next_Tick()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                TestBehaviorNodes.ProbeUntil(2, NodeState.Running, NodeState.Success),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Resumes_From_Running_Child_Skipping_Completed_Siblings()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                TestBehaviorNodes.Probe(slot: 0),
                TestBehaviorNodes.ProbeUntil(2, NodeState.Running, NodeState.Success, slot: 1)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // Tick 1: first child succeeds, second child returns running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.ProbeCount(0)).IsEqualTo(1);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(1);

        // Tick 2: first child already completed (not re-ticked), second child now succeeds
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount(0)).IsEqualTo(1);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(2);
    }

    // SelectorNode

    [Test]
    public async Task Selector_All_Children_Fail_Returns_Failure()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                new Failure(),
                new Failure(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_First_Child_Succeeds_Stops_Immediately()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                new Success(),
                TestBehaviorNodes.Probe(slot: 1, result: NodeState.Failure)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(0);
    }

    [Test]
    public async Task Selector_Running_Child_Resumes_On_Next_Tick()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                TestBehaviorNodes.ProbeUntil(2, NodeState.Running, NodeState.Success),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Selector_Skips_Failed_Children_And_Tries_Next()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                new Failure(),
                new Failure(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ParallelNode

    [Test]
    public async Task Parallel_All_Children_Succeed_Returns_Success()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new Success(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Parallel_All_Children_Fail_Returns_Failure()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Failure(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Running_Takes_Priority_Over_Success_And_Failure()
    {
        using var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new Running(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // RepeatTimesNode

    [Test]
    public async Task RepeatTimes_Zero_Repeats_Ticks_Child_Once_Then_Succeeds()
    {
        using var tree = BTreeNode.Build(
            new Repeat(
                0,
                TestBehaviorNodes.Probe()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount()).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatTimes_BreakStates_Stops_On_Failure()
    {
        using var tree = BTreeNode.Build(
            new Repeat(
                tickTimes: 5,
                TestBehaviorNodes.ProbeUntil(2, NodeState.Success, NodeState.Failure),
                breakStates: NodeState.Failure));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // Tick 1: child returns Success, TickTimes 5->4 -> Running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Tick 2: child returns Failure, break -> Failure
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
        await Assert.That(instance.ProbeCount(0)).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatTimes_One_Repeat_Succeeds_On_First_Completion()
    {
        using var tree = BTreeNode.Build(
            new Repeat(
                1,
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // RepeatForeverNode

    [Test]
    public async Task RepeatForever_Keeps_Running_On_Child_Success()
    {
        using var tree = BTreeNode.Build(
            new RepeatForever(
                default,
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task RepeatForever_Keeps_Running_On_Child_Failure()
    {
        using var tree = BTreeNode.Build(
            new RepeatForever(
                default,
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task RepeatForever_BreakStates_Stops_On_Failure()
    {
        using var tree = BTreeNode.Build(
            new RepeatForever(
                NodeState.Failure,
                TestBehaviorNodes.ProbeUntil(3, NodeState.Success, NodeState.Failure)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task RepeatForever_BreakStates_Stops_On_Success()
    {
        using var tree = BTreeNode.Build(
            new RepeatForever(
                NodeState.Success,
                TestBehaviorNodes.ProbeUntil(2, NodeState.Failure, NodeState.Success)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    [Arguments(NodeState.Success, NodeState.Failure)]
    [Arguments(NodeState.Failure, NodeState.Success)]
    [Arguments(NodeState.Running, NodeState.Running)]
    public async Task Inverter_Maps_Child_State(NodeState childState, NodeState expected)
    {
        using var tree = BTreeNode.Build(new Inverter(TestBehaviorNodes.Constant(childState)));
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(expected);
    }

    [Test]
    [Arguments(NodeState.Failure, NodeState.Success)]
    [Arguments(NodeState.Success, NodeState.Success)]
    [Arguments(NodeState.Running, NodeState.Running)]
    public async Task Succeeder_Maps_Child_State(NodeState childState, NodeState expected)
    {
        using var tree = BTreeNode.Build(new Succeeder(TestBehaviorNodes.Constant(childState)));
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(expected);
    }

    [Test]
    [Arguments(NodeState.Success)]
    [Arguments(NodeState.Failure)]
    [Arguments(NodeState.Running)]
    public async Task Constant_Leaf_Returns_Configured_State(NodeState expected)
    {
        using var tree = BTreeNode.Build(TestBehaviorNodes.Constant(expected));
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(expected);
    }

    // NodeState Extensions

    [Test]
    public async Task NodeState_IsCompleted_True_For_Success_And_Failure()
    {
        await Assert.That(NodeState.Success.IsCompleted()).IsTrue();
        await Assert.That(NodeState.Failure.IsCompleted()).IsTrue();
        await Assert.That(NodeState.Running.IsCompleted()).IsFalse();
    }

    [Test]
    public async Task NodeState_HasFlagFast_Works_With_Combined_Flags()
    {
        NodeState combined = NodeState.Success | NodeState.Failure;
        await Assert.That(combined.HasFlagFast(NodeState.Success)).IsTrue();
        await Assert.That(combined.HasFlagFast(NodeState.Failure)).IsTrue();
        await Assert.That(combined.HasFlagFast(NodeState.Running)).IsFalse();
    }

    [Test]
    public async Task NodeState_ToNodeState_Converts_Bool()
    {
        await Assert.That(true.ToNodeState()).IsEqualTo(NodeState.Success);
        await Assert.That(false.ToNodeState()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task NodeState_IsRunningOrFailure_Returns_Correct_Values()
    {
        await Assert.That(NodeState.Running.IsRunningOrFailure()).IsTrue();
        await Assert.That(NodeState.Failure.IsRunningOrFailure()).IsTrue();
        await Assert.That(NodeState.Success.IsRunningOrFailure()).IsFalse();
    }

    [Test]
    public async Task NodeState_IsRunningOrSuccess_Returns_Correct_Values()
    {
        await Assert.That(NodeState.Running.IsRunningOrSuccess()).IsTrue();
        await Assert.That(NodeState.Success.IsRunningOrSuccess()).IsTrue();
        await Assert.That(NodeState.Failure.IsRunningOrSuccess()).IsFalse();
    }
}

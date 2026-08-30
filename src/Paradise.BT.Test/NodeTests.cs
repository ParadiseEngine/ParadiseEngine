using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
namespace Paradise.BT.Test;

public sealed class NodeTests
{
    // ============================
    // SequenceNode
    // ============================

    [Test]
    public async Task Sequence_All_Children_Succeed_Returns_Success()
    {
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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

    // ============================
    // SelectorNode
    // ============================

    [Test]
    public async Task Selector_All_Children_Fail_Returns_Failure()
    {
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
            new Selector(
                new Failure(),
                new Failure(),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // ParallelNode
    // ============================

    [Test]
    public async Task Parallel_All_Children_Succeed_Returns_Success()
    {
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Failure(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Parallel_Running_Takes_Priority_Over_Success_And_Failure()
    {
        var tree = BTreeNode.Build(
            new global::Paradise.BT.Nodes.Builder.Parallel(
                new Success(),
                new Running(),
                new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // RepeatTimesNode
    // ============================

    [Test]
    public async Task RepeatTimes_Zero_Repeats_Returns_Success_Immediately()
    {
        var tree = BTreeNode.Build(
            new Repeat(
                0,
                TestBehaviorNodes.Probe()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // With 0 repeats, child is ticked once and TickTimes goes from 0 to -1, returning Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task RepeatTimes_BreakStates_Stops_On_Failure()
    {
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
            new Repeat(
                1,
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // RepeatForeverNode
    // ============================

    [Test]
    public async Task RepeatForever_Keeps_Running_On_Child_Success()
    {
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
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
        var tree = BTreeNode.Build(
            new RepeatForever(
                NodeState.Success,
                TestBehaviorNodes.ProbeUntil(2, NodeState.Failure, NodeState.Success)));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // InverterNode
    // ============================

    [Test]
    public async Task Inverter_Inverts_Success_To_Failure()
    {
        var tree = BTreeNode.Build(
            new Inverter(new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Inverter_Inverts_Failure_To_Success()
    {
        var tree = BTreeNode.Build(
            new Inverter(new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Inverter_Passes_Through_Running()
    {
        var tree = BTreeNode.Build(
            new Inverter(new Running()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // SucceederNode
    // ============================

    [Test]
    public async Task Succeeder_Converts_Failure_To_Success()
    {
        var tree = BTreeNode.Build(
            new Succeeder(new Failure()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Passes_Through_Success()
    {
        var tree = BTreeNode.Build(
            new Succeeder(new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Passes_Through_Running()
    {
        var tree = BTreeNode.Build(
            new Succeeder(new Running()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // SuccessNode, FailedNode, RunningNode
    // ============================

    [Test]
    public async Task SuccessNode_Always_Returns_Success()
    {
        var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task FailedNode_Always_Returns_Failure()
    {
        var tree = BTreeNode.Build(new Failure());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task RunningNode_Always_Returns_Running()
    {
        var tree = BTreeNode.Build(new Running());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    // ============================
    // NodeState Extensions
    // ============================

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

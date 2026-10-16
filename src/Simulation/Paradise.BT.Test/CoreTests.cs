using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class CoreTests
{
    // BTreeNode compilation

    [Test]
    public async Task Builder_Accepts_Node_With_Zero_Children()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(new Sequence());

        await Assert.That(tree.Blob.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Builder_Accepts_Node_With_One_Child()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(new Inverter(new Success()));

        await Assert.That(tree.Blob.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Builder_Accepts_Node_With_Multiple_Children()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(new Sequence(new Success(), new Failure()));

        await Assert.That(tree.Blob.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Instance_Build_Produces_Expected_Node_Count()
    {
        var root = new Sequence(
            new Success(),
            new Failure());

        using BehaviorTreeLayout tree = root.Build();

        await Assert.That(tree.Blob.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Build_Null_Root_Throws_ArgumentNullException()
    {
        await Assert.That(() => BTreeNode.Build(null!)).Throws<ArgumentNullException>();
    }

    // Layout topology

    [Test]
    public async Task BehaviorTree_Count_Matches_Total_Node_Count()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Inverter(new Failure())));

        await Assert.That(tree.Blob.Count).IsEqualTo(4);
    }

    [Test]
    public async Task BehaviorTree_GetNodeType_Returns_Correct_Types()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success()));

        await Assert.That(tree.GetNodeType(0)).IsEqualTo(typeof(SequenceNode));
        await Assert.That(tree.GetNodeType(1)).IsEqualTo(typeof(SuccessNode));
    }

    [Test]
    public async Task BehaviorTree_GetEndIndex_Returns_Correct_Indices()
    {
        using BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Failure()));

        await Assert.That(tree.GetEndIndex(0)).IsEqualTo(3);
        await Assert.That(tree.GetEndIndex(1)).IsEqualTo(2);
        await Assert.That(tree.GetEndIndex(2)).IsEqualTo(3);
    }

    // Test instance lifecycle

    [Test]
    public async Task Instance_Status_Reflects_Last_Tick_Result()
    {
        using var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();

        await Assert.That(instance.Status).IsEqualTo(NodeState.Success);
    }

    [Test]
    [Arguments(false, NodeState.Success)]
    [Arguments(true, NodeState.Running)]
    public async Task Instance_AutoReset_Controls_Completed_Node_Data(bool autoReset, NodeState expectedAfterCompletion)
    {
        using var tree = BTreeNode.Build(new Repeat(2, TestBehaviorNodes.Probe()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = autoReset;

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);

        // Only a reset restores the repeat count; blackboard observations survive either path.
        await Assert.That(instance.Tick()).IsEqualTo(expectedAfterCompletion);
        await Assert.That(instance.Status).IsEqualTo(expectedAfterCompletion);
        await Assert.That(instance.ProbeCount()).IsEqualTo(3);
    }

    [Test]
    public async Task Instance_Reset_Clears_State_To_None()
    {
        using var tree = BTreeNode.Build(new Running());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();
        await Assert.That(instance.Status).IsEqualTo(NodeState.Running);

        instance.Reset();
        await Assert.That(instance.Status).IsEqualTo(NodeState.None);
    }

    [Test]
    public async Task Instance_With_An_Empty_Blackboard_Works()
    {
        using var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_Blackboard_Ref_Is_Accessible()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(42);

        using var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Blackboard.GetData<int>()).IsEqualTo(42);
    }

    // Complex tree scenarios

    [Test]
    public async Task Deep_Nested_Tree_Executes_Correctly()
    {
        using var tree = BTreeNode.Build(
            new Sequence(
                new Inverter(new Failure()),
                new Succeeder(new Failure())));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Selector_With_Running_Then_Success_Returns_Running_First()
    {
        using var tree = BTreeNode.Build(
            new Selector(
                TestBehaviorNodes.ProbeUntil(2, NodeState.Running, NodeState.Failure),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

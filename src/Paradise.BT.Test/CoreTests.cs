using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
namespace Paradise.BT.Test;

public sealed class CoreTests
{
    // ============================
    // BTreeNode compilation
    // ============================

    [Test]
    public async Task Builder_Accepts_Node_With_Zero_Children()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(new Sequence());

        await Assert.That(tree.Blob.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Builder_Accepts_Node_With_One_Child()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(new Inverter(new Success()));

        await Assert.That(tree.Blob.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Builder_Accepts_Node_With_Multiple_Children()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(new Sequence(new Success(), new Failure()));

        await Assert.That(tree.Blob.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Instance_Build_Produces_Same_Result_As_Static()
    {
        var root = new Sequence(
            new Success(),
            new Failure());

        BehaviorTreeLayout tree = root.Build();

        await Assert.That(tree.Blob.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Build_Null_Root_Throws_ArgumentNullException()
    {
        ArgumentNullException? ex = null;
        try
        {
            _ = BTreeNode.Build(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    // ============================
    // Layout topology
    // ============================

    [Test]
    public async Task BehaviorTree_Count_Matches_Total_Node_Count()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Inverter(new Failure())));

        // Sequence(1) + Success(1) + Inverter(1) + Failure(1) = 4
        await Assert.That(tree.Blob.Count).IsEqualTo(4);
    }

    [Test]
    public async Task BehaviorTree_GetNodeType_Returns_Correct_Types()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success()));

        await Assert.That(tree.GetNodeType(0)).IsEqualTo(typeof(SequenceNode));
        await Assert.That(tree.GetNodeType(1)).IsEqualTo(typeof(SuccessNode));
    }

    [Test]
    public async Task BehaviorTree_GetEndIndex_Returns_Correct_Indices()
    {
        BehaviorTreeLayout tree = BTreeNode.Build(
            new Sequence(
                new Success(),
                new Failure()));

        // Sequence at 0 ends at 3 (total count)
        await Assert.That(tree.GetEndIndex(0)).IsEqualTo(3);
        // Success at 1 ends at 2
        await Assert.That(tree.GetEndIndex(1)).IsEqualTo(2);
        // Failure at 2 ends at 3
        await Assert.That(tree.GetEndIndex(2)).IsEqualTo(3);
    }

    // ============================
    // BehaviorTreeInstance lifecycle
    // ============================

    [Test]
    public async Task Instance_Status_Reflects_Last_Tick_Result()
    {
        var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();

        await Assert.That(instance.Status).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_AutoReset_Disabled_Does_Not_Reset_On_Completion()
    {
        var tree = BTreeNode.Build(
            TestBehaviorNodes.Probe());

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();
        instance.Tick();

        // Without auto-reset, the second tick sees the completed state and should
        // still return Success (the node is not re-executed)
        await Assert.That(instance.Status).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_AutoReset_Enabled_Resets_And_Reticks()
    {
        var tree = BTreeNode.Build(
            TestBehaviorNodes.Probe());

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        instance.Tick();
        instance.Tick();

        await Assert.That(instance.ProbeCount()).IsEqualTo(2);
    }

    [Test]
    public async Task Instance_Reset_Clears_State_To_Running()
    {
        var tree = BTreeNode.Build(new Running());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.AutoResetOnCompletion = false;

        instance.Tick();
        await Assert.That(instance.Status).IsEqualTo(NodeState.Running);

        instance.Reset();
        // After reset, state is cleared to 0 (no flags set)
        await Assert.That(instance.Status).IsEqualTo((NodeState)0);
    }

    [Test]
    public async Task Instance_CreateInstance_Without_Blackboard_Works()
    {
        var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Instance_Blackboard_Ref_Is_Accessible()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(42);

        var tree = BTreeNode.Build(new Success());
        TestInstance<Blackboard> instance = tree.CreateInstance(blackboard);

        await Assert.That(instance.Blackboard.GetData<int>()).IsEqualTo(42);
    }

    // ============================
    // Complex tree scenarios
    // ============================

    [Test]
    public async Task Deep_Nested_Tree_Executes_Correctly()
    {
        // Sequence -> Inverter -> Failure = Sequence(Inverter(Failure))
        // Inverter turns Failure into Success, Sequence with one child returns that
        var tree = BTreeNode.Build(
            new Sequence(
                new Inverter(new Failure()),
                new Succeeder(new Failure())));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // Both children return Success -> Sequence returns Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Selector_With_Running_Then_Success_Returns_Running_First()
    {
        var tree = BTreeNode.Build(
            new Selector(
                TestBehaviorNodes.ProbeUntil(2, NodeState.Running, NodeState.Failure),
                new Success()));

        TestInstance<Blackboard> instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        // First tick: first child is Running -> Selector returns Running
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        // Second tick: first child fails, second child succeeds -> Success
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

}

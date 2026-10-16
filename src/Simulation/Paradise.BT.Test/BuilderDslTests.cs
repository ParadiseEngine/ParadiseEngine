using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class BuilderDslTests
{
    [Test]
    [Arguments(NodeState.Success)]
    [Arguments(NodeState.Failure)]
    [Arguments(NodeState.Running)]
    public async Task Leaf_Builds_Same_As_Factory(NodeState expected)
    {
        using var factoryTree = BTreeNode.Build(TestBehaviorNodes.Constant(expected));
        using var builderTree = TestBehaviorNodes.Constant(expected).Build();

        var factoryInstance = factoryTree.CreateInstance(new Blackboard());
        var builderInstance = builderTree.CreateInstance(new Blackboard());

        await Assert.That(factoryInstance.Tick()).IsEqualTo(expected);
        await Assert.That(builderInstance.Tick()).IsEqualTo(expected);
    }

    [Test]
    public async Task Sequence_With_Success_Children()
    {
        using var tree = new Sequence(new Success(), new Success()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Fails_On_First_Failure()
    {
        using var tree = new Sequence(new Success(), new Failure(), new Success()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_Succeeds_On_First_Success()
    {
        using var tree = new Selector(new Failure(), new Success(), new Failure()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    [Arguments(NodeState.Success, NodeState.Failure)]
    [Arguments(NodeState.Failure, NodeState.Success)]
    [Arguments(NodeState.Running, NodeState.Running)]
    public async Task Inverter_Maps_Child_State(NodeState childState, NodeState expected)
    {
        using var tree = new Inverter(TestBehaviorNodes.Constant(childState)).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(expected);
    }

    [Test]
    public async Task Succeeder_Converts_Failure_To_Success()
    {
        using var tree = new Succeeder(new Failure()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Repeat_Completes_After_Configured_Times()
    {
        using var tree = new Repeat(3, TestBehaviorNodes.Probe()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount()).IsEqualTo(3);
    }

    [Test]
    public async Task Nested_Tree_Matches_Factory_Behavior()
    {
        using var factoryTree = BTreeNode.Build(
            new Selector(
                new Sequence(
                    new Success(),
                    new Failure()),
                new Success()));

        using var dslTree = new Selector(
            new Sequence(
                new Success(),
                new Failure()),
            new Success()
        ).Build();

        var factoryInstance = factoryTree.CreateInstance(new Blackboard());
        var dslInstance = dslTree.CreateInstance(new Blackboard());

        await Assert.That(factoryInstance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(dslInstance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Parallel_Runs_All_Children()
    {
        using var tree = new Paradise.BT.Nodes.Builder.Parallel(
            TestBehaviorNodes.Probe(slot: 0),
            TestBehaviorNodes.Probe(slot: 1)
        ).Build();

        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount(0)).IsEqualTo(1);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(1);
    }
}

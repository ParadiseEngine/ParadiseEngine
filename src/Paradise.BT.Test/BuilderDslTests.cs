using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class BuilderDslTests
{
    [Test]
    public async Task Leaf_Success_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(new Success());
        var builderTree = new Success().Build();

        var factoryInstance = factoryTree.CreateInstance(new Blackboard());
        var builderInstance = builderTree.CreateInstance(new Blackboard());

        await Assert.That(factoryInstance.Tick()).IsEqualTo(builderInstance.Tick());
        await Assert.That(builderInstance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Leaf_Failure_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(new Failure());
        var builderTree = new Failure().Build();

        await Assert.That(builderTree.CreateInstance(new Blackboard()).Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Leaf_Running_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(new Running());
        var builderTree = new Running().Build();

        await Assert.That(builderTree.CreateInstance(new Blackboard()).Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task Sequence_With_Success_Children()
    {
        var tree = new Sequence(new Success(), new Success()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Fails_On_First_Failure()
    {
        var tree = new Sequence(new Success(), new Failure(), new Success()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_Succeeds_On_First_Success()
    {
        var tree = new Selector(new Failure(), new Success(), new Failure()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Inverter_Flips_Success_To_Failure()
    {
        var tree = new Inverter(new Success()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Inverter_Flips_Failure_To_Success()
    {
        var tree = new Inverter(new Failure()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Converts_Failure_To_Success()
    {
        var tree = new Succeeder(new Failure()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Repeat_Completes_After_Configured_Times()
    {

        var tree = new Repeat(
            3,
            new LeafNode<CounterNode>(new CounterNode())
        ).Build();

        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(instance.ProbeCount()).IsEqualTo(3);
    }

    [Test]
    public async Task Nested_Tree_Matches_Factory_Behavior()
    {
        // Build with factory
        var factoryTree = BehaviorTreeBuilder.Build(
            new Selector(
                new Sequence(
                    new Success(),
                    new Failure()),
                new Success()));

        // Build with DSL
        var dslTree = new Selector(
            new Sequence(
                new Success(),
                new Failure()),
            new Success()
        ).Build();

        var factoryInstance = factoryTree.CreateInstance(new Blackboard());
        var dslInstance = dslTree.CreateInstance(new Blackboard());

        // Both should follow: sequence(success, failure) -> failure, then selector tries success -> success
        await Assert.That(factoryInstance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(dslInstance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Build_Method_On_Any_Node_Produces_Valid_Tree()
    {
        // Build from a non-root node
        var tree = new Inverter(new Running()).Build();
        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task Parallel_Runs_All_Children()
    {

        var tree = new Paradise.BT.Nodes.Builder.Parallel(
            new LeafNode<CounterNode>(new CounterNode { Slot = 0 }),
            new LeafNode<CounterNode>(new CounterNode { Slot = 1 })
        ).Build();

        var instance = tree.CreateInstance(TestBehaviorNodes.NewBlackboard());
        instance.Tick();

        await Assert.That(instance.ProbeCount(0)).IsEqualTo(1);
        await Assert.That(instance.ProbeCount(1)).IsEqualTo(1);
    }

    // Counts into a blackboard slot. It used to hold an Action and say "not serializable due to
    // delegate, but fine for tests" — it was the last node in the library whose data was managed,
    // and so could not live in a blob at all.
    [System.Runtime.InteropServices.Guid("E1234567-ABCD-4321-FEDC-BA9876543210")]
    [Writes<ProbeData>]
    internal struct CounterNode : INodeData
    {
        public int Slot;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
            where TNodeBlob : struct, INodeBlob, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            var probe = bb.GetData<ProbeData>();
            probe.Counts[Slot]++;
            bb.SetData(probe);
            return NodeState.Success;
        }
    }
}

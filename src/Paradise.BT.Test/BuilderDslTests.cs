using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

public sealed class BuilderDslTests
{
    [Test]
    public async Task Leaf_Success_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Success());
        var builderTree = new Success().Build();

        var factoryInstance = factoryTree.CreateInstance(new Blackboard());
        var builderInstance = builderTree.CreateInstance(new Blackboard());

        await Assert.That(factoryInstance.Tick()).IsEqualTo(builderInstance.Tick());
        await Assert.That(builderInstance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Leaf_Failure_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Failure());
        var builderTree = new Failure().Build();

        await Assert.That(builderTree.CreateInstance(new Blackboard()).Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Leaf_Running_Builds_Same_As_Factory()
    {
        var factoryTree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Running());
        var builderTree = new Running().Build();

        await Assert.That(builderTree.CreateInstance(new Blackboard()).Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task Sequence_With_Success_Children()
    {
        var tree = new Sequence(new Success(), new Success()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Sequence_Fails_On_First_Failure()
    {
        var tree = new Sequence(new Success(), new Failure(), new Success()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Selector_Succeeds_On_First_Success()
    {
        var tree = new Selector(new Failure(), new Success(), new Failure()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Inverter_Flips_Success_To_Failure()
    {
        var tree = new Inverter(new Success()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Failure);
    }

    [Test]
    public async Task Inverter_Flips_Failure_To_Success()
    {
        var tree = new Inverter(new Failure()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Succeeder_Converts_Failure_To_Success()
    {
        var tree = new Succeeder(new Failure()).Build();
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Repeat_Completes_After_Configured_Times()
    {
        int executions = 0;
        var tree = new Repeat(
            3,
            new LeafNode<CounterNode>(new CounterNode { Callback = () => executions++ })
        ).Build();

        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        await Assert.That(executions).IsEqualTo(3);
    }

    [Test]
    public async Task Nested_Tree_Matches_Factory_Behavior()
    {
        // Build with factory
        var factoryTree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Selector(
                BuiltInBehaviorNodes.Sequence(
                    BuiltInBehaviorNodes.Success(),
                    BuiltInBehaviorNodes.Failure()),
                BuiltInBehaviorNodes.Success()));

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
        var instance = tree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task Parallel_Runs_All_Children()
    {
        int count = 0;
        var tree = new Paradise.BT.Nodes.Builder.Parallel(
            new LeafNode<CounterNode>(new CounterNode { Callback = () => count++ }),
            new LeafNode<CounterNode>(new CounterNode { Callback = () => count++ })
        ).Build();

        var instance = tree.CreateInstance(new Blackboard());
        instance.Tick();

        await Assert.That(count).IsEqualTo(2);
    }

    // Helper node for counting executions
    [System.Runtime.InteropServices.Guid("E1234567-ABCD-4321-FEDC-BA9876543210")]
    private struct CounterNode : INodeData
    {
        // Not serializable due to delegate, but fine for tests
        public Action? Callback;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            Callback?.Invoke();
            return NodeState.Success;
        }
    }
}

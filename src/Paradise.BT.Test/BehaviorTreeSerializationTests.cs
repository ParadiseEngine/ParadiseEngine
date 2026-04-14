namespace Paradise.BT.Test;

public sealed class BehaviorTreeSerializationTests
{
    [System.Runtime.InteropServices.Guid("8C751F7C-3CAA-4E55-BEBA-96DEB5F8C9A5")]
    private struct ThresholdNode : INodeData
    {
        public int RequiredTicks;
        public int Count;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
            where TNodeBlob : struct, INodeBlob
            where TBlackboard : struct, IBlackboard
        {
            Count++;
            return Count >= RequiredTicks ? NodeState.Success : NodeState.Running;
        }
    }

    [Test]
    public async Task Built_In_Trees_Can_Roundtrip_Through_Blob_Serialization()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Sequence(
                BehaviorNodes.Delay(0.5f),
                BehaviorNodes.Repeat(2, BehaviorNodes.Success())));

        using var serializedTree = tree.Serialize();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(serializedTree);
        BehaviorTreeInstance instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick(0.25f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.25f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Custom_Unmanaged_Nodes_Can_Be_Deserialized_With_A_Registry()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 3 }));

        using var serializedTree = tree.Serialize();
        var registry = new BehaviorTreeSerializationRegistry().Register<ThresholdNode>();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(serializedTree, registry);
        BehaviorTreeInstance instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Delegate_Backed_Nodes_Are_Rejected_By_Blob_Serialization()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Action(_ => NodeState.Success));

        Exception? exception = null;
        IDisposable? serializedTree = null;

        try
        {
            serializedTree = tree.Serialize();
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            serializedTree?.Dispose();
        }

        await Assert.That(exception is NotSupportedException).IsTrue();
        await Assert.That(exception?.Message.Contains(nameof(DelegateActionNode), StringComparison.Ordinal) ?? false).IsTrue();
    }
}

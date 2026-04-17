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
            TestBehaviorNodes.Sequence(
                TestBehaviorNodes.Delay(0.5f),
                TestBehaviorNodes.Repeat(2, TestBehaviorNodes.Success())));

        using var serializedTree = tree.Serialize();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(serializedTree, TestBehaviorNodes.BuiltInRegistry());
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
        var tree = BehaviorTreeBuilder.Build(TestBehaviorNodes.Action(_ => NodeState.Success));

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

    // ============================
    // Byte array serialization
    // ============================

    [Test]
    public async Task SerializeToBytes_And_Deserialize_From_Bytes_Round_Trips()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Selector(
                TestBehaviorNodes.Failure(),
                TestBehaviorNodes.Success()));

        byte[] bytes = tree.SerializeToBytes();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(bytes, TestBehaviorNodes.BuiltInRegistry());
        BehaviorTreeInstance instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task SerializeToBytes_Preserves_Node_Count()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Sequence(
                TestBehaviorNodes.Success(),
                TestBehaviorNodes.Failure()));

        byte[] bytes = tree.SerializeToBytes();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(bytes, TestBehaviorNodes.BuiltInRegistry());

        await Assert.That(roundTrippedTree.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Serialize_From_Definition_Matches_Serialize_From_Tree()
    {
        var definition = TestBehaviorNodes.Sequence(
            TestBehaviorNodes.Success(),
            TestBehaviorNodes.Success());

        using var fromDefinition = BehaviorTreeBlobSerializer.Serialize(definition);
        BehaviorTree tree1 = BehaviorTreeBlobSerializer.Deserialize(fromDefinition, TestBehaviorNodes.BuiltInRegistry());

        BehaviorTree compiled = BehaviorTreeBuilder.Build(definition);
        using var fromTree = BehaviorTreeBlobSerializer.Serialize(compiled);
        BehaviorTree tree2 = BehaviorTreeBlobSerializer.Deserialize(fromTree, TestBehaviorNodes.BuiltInRegistry());

        await Assert.That(tree1.Count).IsEqualTo(tree2.Count);
    }

    [Test]
    public async Task SerializeToBytes_From_Definition_Works()
    {
        var definition = TestBehaviorNodes.Sequence(TestBehaviorNodes.Success());

        byte[] bytes = BehaviorTreeBlobSerializer.SerializeToBytes(definition);
        BehaviorTree tree = BehaviorTreeBlobSerializer.Deserialize(bytes, TestBehaviorNodes.BuiltInRegistry());

        await Assert.That(tree.Count).IsEqualTo(2);
    }

    // ============================
    // DelegateConditionNode serialization rejection
    // ============================

    [Test]
    public async Task Delegate_Condition_Nodes_Are_Rejected_By_Blob_Serialization()
    {
        var tree = BehaviorTreeBuilder.Build(TestBehaviorNodes.Condition(_ => true));

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
        await Assert.That(exception?.Message.Contains(nameof(DelegateConditionNode), StringComparison.Ordinal) ?? false).IsTrue();
    }

    // ============================
    // Registry
    // ============================

    [Test]
    public async Task Registry_Missing_Guid_Throws_On_Deserialize()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 1 }));
        using var serializedTree = tree.Serialize();

        // Default registry is empty — no built-in nodes are auto-registered.
        var emptyRegistry = new BehaviorTreeSerializationRegistry();

        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBlobSerializer.Deserialize(serializedTree, emptyRegistry);
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("not registered", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Registry_With_All_Standard_Nodes_Handles_Every_Built_In_Type()
    {
        // Build a tree using every built-in node type
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Sequence(
                TestBehaviorNodes.Selector(TestBehaviorNodes.Failure(), TestBehaviorNodes.Success()),
                TestBehaviorNodes.Parallel(TestBehaviorNodes.Success(), TestBehaviorNodes.Running()),
                TestBehaviorNodes.Repeat(1, TestBehaviorNodes.Success()),
                TestBehaviorNodes.RepeatForever(TestBehaviorNodes.Failure(), breakStates: NodeState.Failure),
                TestBehaviorNodes.Inverter(TestBehaviorNodes.Success()),
                TestBehaviorNodes.Succeeder(TestBehaviorNodes.Failure()),
                TestBehaviorNodes.Delay(0.1f)));

        // Should not throw - all node types are registered via the full built-in registry
        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized, TestBehaviorNodes.BuiltInRegistry());

        await Assert.That(roundTripped.Count).IsEqualTo(tree.Count);
    }

    [Test]
    public async Task Registry_Re_Register_Same_Type_Is_Idempotent()
    {
        var registry = new BehaviorTreeSerializationRegistry();
        // Registering the same type again should not throw
        registry.Register<ThresholdNode>();
        registry.Register<ThresholdNode>();

        // Verify it works
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 1 }));
        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized, registry);

        await Assert.That(roundTripped.Count).IsEqualTo(1);
    }

    // ============================
    // Complex tree round-trip
    // ============================

    [Test]
    public async Task Complex_Tree_Round_Trip_Preserves_Behavior()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Sequence(
                TestBehaviorNodes.Inverter(TestBehaviorNodes.Failure()),
                TestBehaviorNodes.Repeat(2, TestBehaviorNodes.Delay(0.1f))));

        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized, TestBehaviorNodes.BuiltInRegistry());
        BehaviorTreeInstance instance = roundTripped.CreateInstance(new Blackboard());

        // Inverter(Failure) -> Success
        // Repeat(2, Delay(0.1)) -> needs 2 completions of the delay
        // Tick with 0.05: Inverter succeeds, Delay running -> Sequence running
        await Assert.That(instance.Tick(0.05f)).IsEqualTo(NodeState.Running);
        // Tick with 0.1: Delay completes (1st), repeat resets child, re-ticks delay running
        await Assert.That(instance.Tick(0.1f)).IsEqualTo(NodeState.Running);
        // Tick with 0.1: Delay completes (2nd), repeat done -> Sequence success
        await Assert.That(instance.Tick(0.1f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Round_Trip_Preserves_Node_Types()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Sequence(
                TestBehaviorNodes.Inverter(TestBehaviorNodes.Success())));

        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized, TestBehaviorNodes.BuiltInRegistry());

        await Assert.That(roundTripped.GetNodeType(0)).IsEqualTo(typeof(SequenceNode));
        await Assert.That(roundTripped.GetNodeType(1)).IsEqualTo(typeof(InverterNode));
        await Assert.That(roundTripped.GetNodeType(2)).IsEqualTo(typeof(SuccessNode));
    }

    [Test]
    public async Task Deserialize_Rejects_Wrong_Format_Version()
    {
        var tree = BehaviorTreeBuilder.Build(
            TestBehaviorNodes.Sequence(TestBehaviorNodes.Success()));

        byte[] blob = tree.SerializeToBytes();
        // Corrupt the format version (first 4 bytes of the blob struct)
        blob[0] = 0xFF;

        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBlobSerializer.Deserialize(blob);
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("format version", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task Custom_Node_Default_Data_Preserved_Through_Round_Trip()
    {
        var tree = BehaviorTreeBuilder.Build(
            BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 5 }));

        using var serialized = tree.Serialize();
        var registry = new BehaviorTreeSerializationRegistry().Register<ThresholdNode>();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized, registry);
        BehaviorTreeInstance instance = roundTripped.CreateInstance(new Blackboard());

        // Needs 5 ticks to complete
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        }

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
namespace Paradise.BT.Test;

public sealed class BehaviorTreeSerializationTests
{
    [System.Runtime.InteropServices.Guid("8C751F7C-3CAA-4E55-BEBA-96DEB5F8C9A5")]
    internal struct ThresholdNode : INodeData
    {
        public int RequiredTicks;
        public int Count;

        public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
            where TNodeBlob : struct, INodeBlob, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            Count++;
            return Count >= RequiredTicks ? NodeState.Success : NodeState.Running;
        }
    }

    [Test]
    public async Task Built_In_Trees_Can_Roundtrip_Through_Blob_Serialization()
    {
        var tree = BehaviorTreeBuilder.Build(
            new Sequence(
                new Delay(0.5f),
                new Repeat(2, new Success())));

        using var serializedTree = tree.Serialize();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(serializedTree);
        BehaviorTreeInstance<Blackboard> instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick(0.25f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.25f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    /// <summary>No registry to populate: <see cref="ThresholdNode"/> is internal, so the
    /// generated module initializer registered it, and deserialization resolves through the same
    /// <see cref="NodeTypeRegistry"/> everything else does.</summary>
    [Test]
    public async Task Custom_Unmanaged_Nodes_Deserialize_Through_The_Type_Registry()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 3 }));

        using var serializedTree = tree.Serialize();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(serializedTree);
        BehaviorTreeInstance<Blackboard> instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    // ============================
    // Byte array serialization
    // ============================

    [Test]
    public async Task SerializeToBytes_And_Deserialize_From_Bytes_Round_Trips()
    {
        var tree = BehaviorTreeBuilder.Build(
            new Selector(
                new Failure(),
                new Success()));

        byte[] bytes = tree.SerializeToBytes();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(bytes);
        BehaviorTreeInstance<Blackboard> instance = roundTrippedTree.CreateInstance(new Blackboard());

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task SerializeToBytes_Preserves_Node_Count()
    {
        var tree = BehaviorTreeBuilder.Build(
            new Sequence(
                new Success(),
                new Failure()));

        byte[] bytes = tree.SerializeToBytes();
        BehaviorTree roundTrippedTree = BehaviorTreeBlobSerializer.Deserialize(bytes);

        await Assert.That(roundTrippedTree.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Serialize_From_Definition_Matches_Serialize_From_Tree()
    {
        var definition = new Sequence(
            new Success(),
            new Success());

        using var fromDefinition = BehaviorTreeBlobSerializer.Serialize(definition);
        BehaviorTree tree1 = BehaviorTreeBlobSerializer.Deserialize(fromDefinition);

        BehaviorTree compiled = BehaviorTreeBuilder.Build(definition);
        using var fromTree = BehaviorTreeBlobSerializer.Serialize(compiled);
        BehaviorTree tree2 = BehaviorTreeBlobSerializer.Deserialize(fromTree);

        await Assert.That(tree1.Count).IsEqualTo(tree2.Count);
    }

    [Test]
    public async Task SerializeToBytes_From_Definition_Works()
    {
        var definition = new Sequence(new Success());

        byte[] bytes = BehaviorTreeBlobSerializer.SerializeToBytes(definition);
        BehaviorTree tree = BehaviorTreeBlobSerializer.Deserialize(bytes);

        await Assert.That(tree.Count).IsEqualTo(2);
    }


    // ============================
    // Registry
    // ============================

    /// <summary>Serializing needs no registration — the factories come from the definitions — so
    /// a PRIVATE node (which the generator will not register) makes a blob whose GUID nobody in
    /// this process answers to.</summary>
    [Test]
    public async Task Deserialize_Refuses_A_Node_Guid_Nobody_Registered()
    {
        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new HermitNode()));
        using var serializedTree = tree.Serialize();

        InvalidOperationException? ex = null;
        try
        {
            BehaviorTreeBlobSerializer.Deserialize(serializedTree);
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message.Contains("not registered", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Private on purpose, so the generated module initializer cannot name it.</summary>
    [System.Runtime.InteropServices.Guid("2E7D9A3B-5C1F-4E82-9B60-D4A7F1C8E052")]
    private struct HermitNode : INodeData
    {
        public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
            where TNodeBlob : struct, INodeBlob, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
            => NodeState.Success;
    }

    [Test]
    public async Task Registry_With_All_Standard_Nodes_Handles_Every_Built_In_Type()
    {
        // Build a tree using every built-in node type
        var tree = BehaviorTreeBuilder.Build(
            new Sequence(
                new Selector(new Failure(), new Success()),
                new global::Paradise.BT.Nodes.Builder.Parallel(new Success(), new Running()),
                new Repeat(1, new Success()),
                new RepeatForever(NodeState.Failure, new Failure()),
                new Inverter(new Success()),
                new Succeeder(new Failure()),
                new Delay(0.1f)));

        // Should not throw — every built-in node type is registered by its generated initializer
        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized);

        await Assert.That(roundTripped.Count).IsEqualTo(tree.Count);
    }

    /// <summary>Explicit registration on top of the generated one must be harmless — a node the
    /// generator cannot see is registered by hand, and hands may be redundant.</summary>
    [Test]
    public async Task Explicit_Registration_On_Top_Of_The_Generated_One_Is_Idempotent()
    {
        NodeTypeRegistry.Register<ThresholdNode>();
        NodeTypeRegistry.Register<ThresholdNode>();

        var tree = BehaviorTreeBuilder.Build(BehaviorNodes.Node(new ThresholdNode { RequiredTicks = 1 }));
        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized);

        await Assert.That(roundTripped.Count).IsEqualTo(1);
    }

    // ============================
    // Complex tree round-trip
    // ============================

    [Test]
    public async Task Complex_Tree_Round_Trip_Preserves_Behavior()
    {
        var tree = BehaviorTreeBuilder.Build(
            new Sequence(
                new Inverter(new Failure()),
                new Repeat(2, new Delay(0.1f))));

        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized);
        BehaviorTreeInstance<Blackboard> instance = roundTripped.CreateInstance(new Blackboard());

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
            new Sequence(
                new Inverter(new Success())));

        using var serialized = tree.Serialize();
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized);

        await Assert.That(roundTripped.GetNodeType(0)).IsEqualTo(typeof(SequenceNode));
        await Assert.That(roundTripped.GetNodeType(1)).IsEqualTo(typeof(InverterNode));
        await Assert.That(roundTripped.GetNodeType(2)).IsEqualTo(typeof(SuccessNode));
    }

    [Test]
    public async Task Deserialize_Rejects_Wrong_Format_Version()
    {
        var tree = BehaviorTreeBuilder.Build(
            new Sequence(new Success()));

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
        BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(serialized);
        BehaviorTreeInstance<Blackboard> instance = roundTripped.CreateInstance(new Blackboard());

        // Needs 5 ticks to complete
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        }

        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

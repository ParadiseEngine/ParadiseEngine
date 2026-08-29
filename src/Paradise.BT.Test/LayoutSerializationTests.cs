using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

/// <summary>
/// The layout blob is the shippable artifact: <see cref="BehaviorTreeLayout.SerializeToBytes"/> is
/// a raw copy of the position-independent blob, and <see cref="BehaviorTreeLayout.Deserialize"/>
/// maps it back with no managed tree in between. What needs guarding is the load seam — that a
/// loaded layout ticks exactly like the one it was copied from (the dense type ids it carries are
/// meaningless here and must be re-resolved from GUIDs), and that corrupt bytes are refused at
/// load rather than faulting on the first tick.
/// </summary>
public sealed class LayoutSerializationTests
{
    private static BehaviorNodeDefinition SampleTree() =>
        new Selector(
            new Sequence(
                new Success(),
                new Delay(0.5f),
                new Success()),
            new Inverter(new Failure()));

    private static BehaviorTreeLayout LayoutOf(BehaviorNodeDefinition definition) =>
        BehaviorTreeLayout.Build(BehaviorTreeBuilder.Build(definition));

    private static NodeState Tick(BehaviorTreeInstance instance, Blackboard bb, float deltaTime)
    {
        bb.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
        return instance.Tick(bb);
    }

    [Test]
    public async Task A_Loaded_Layout_Ticks_Identically_To_The_Original()
    {
        using BehaviorTreeLayout original = LayoutOf(SampleTree());
        using BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(original.SerializeToBytes());

        BehaviorTreeInstance originalInstance = original.CreateInstance();
        BehaviorTreeInstance loadedInstance = loaded.CreateInstance();
        var bb = new Blackboard();

        for (int i = 0; i < 20; i++)
        {
            NodeState expected = Tick(originalInstance, bb, 0.2f);
            NodeState actual = Tick(loadedInstance, bb, 0.2f);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task A_Loaded_Layout_Preserves_Authored_Defaults()
    {
        using BehaviorTreeLayout original = LayoutOf(new Delay(0.25f));
        using BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(original.SerializeToBytes());

        BehaviorTreeInstance instance = loaded.CreateInstance();
        var bb = new Blackboard();

        // 0.25s of delay authored at build time, restored through the byte round-trip: two 0.1s
        // steps are not enough, the third is.
        await Assert.That(Tick(instance, bb, 0.1f)).IsEqualTo(NodeState.Running);
        await Assert.That(Tick(instance, bb, 0.1f)).IsEqualTo(NodeState.Running);
        await Assert.That(Tick(instance, bb, 0.1f)).IsEqualTo(NodeState.Success);
    }

    /// <summary>The tree route to the same bytes, for callers holding a
    /// <see cref="BehaviorTree"/> rather than a layout.</summary>
    [Test]
    public async Task A_Tree_Serializes_Its_Layout_Directly()
    {
        using var tree = BehaviorTreeBuilder.Build(SampleTree());
        using BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(tree.SerializeLayoutToBytes());

        await Assert.That(loaded.NodeCount).IsEqualTo(tree.Count);
    }

    [Test]
    public async Task Load_Refuses_A_Foreign_Format_Version()
    {
        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        byte[] bytes = layout.SerializeToBytes();

        // FormatVersion is the blob's first field.
        BitConverter.GetBytes(999).CopyTo(bytes, 0);

        await Assert.That(() => BehaviorTreeLayout.Deserialize(bytes))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("version");
    }

    [Test]
    public async Task Load_Refuses_Truncated_Bytes()
    {
        using BehaviorTreeLayout layout = LayoutOf(SampleTree());
        byte[] bytes = layout.SerializeToBytes();

        await Assert.That(() => BehaviorTreeLayout.Deserialize(bytes.AsSpan(0, bytes.Length / 2).ToArray()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Load_Refuses_A_Node_Guid_Nobody_Registered()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Success());
        byte[] bytes = layout.SerializeToBytes();

        // Overwrite the one node's GUID, wherever the blob put it, with one nobody registered.
        var unknown = new Guid("0B9DBE5A-83AC-4C86-97D3-0E3E4D3C2B1A");
        int guidOffset = bytes.AsSpan().IndexOf(typeof(Paradise.BT.Nodes.SuccessNode).GUID.ToByteArray());
        await Assert.That(guidOffset).IsGreaterThanOrEqualTo(0);
        unknown.ToByteArray().CopyTo(bytes, guidOffset);

        await Assert.That(() => BehaviorTreeLayout.Deserialize(bytes))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(unknown.ToString());
    }

    /// <summary>A ref struct blackboard cannot live in a field, so it goes through
    /// <see cref="BehaviorTreeInstance.Tick{TBlackboard}"/> per call — the same shape the
    /// generated bindings need.</summary>
    [Test]
    public async Task An_Instance_Ticks_With_A_Ref_Struct_Blackboard()
    {
        using BehaviorTreeLayout layout = LayoutOf(new Sequence(new Success(), new Success()));
        BehaviorTreeInstance instance = layout.CreateInstance();

        NodeState state = instance.Tick(new RefStructBlackboard());

        await Assert.That(state).IsEqualTo(NodeState.Success);
    }

    private readonly ref struct RefStructBlackboard : IBlackboard
    {
        public bool HasData<T>() where T : struct => false;

        public T GetData<T>() where T : struct => throw new InvalidOperationException();

        public void SetData<T>(T value) where T : struct => throw new InvalidOperationException();
    }
}

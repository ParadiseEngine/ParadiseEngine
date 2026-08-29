using System.Runtime.CompilerServices;
using Paradise.BT.Nodes;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

/// <summary>
/// The unmanaged instance must be a drop-in for the class-based one, and being a plain struct is
/// its whole point: it lives inline in a component and a snapshot copies it by assignment.
/// </summary>
public sealed class FixedInstanceTests
{
    [InlineArray(16)]
    private struct Nodes16
    {
        private NodeState _element;
    }

    [InlineArray(128)]
    private struct Bytes128
    {
        private byte _element;
    }

    private static BehaviorTreeLayout DelayLayout(float seconds) =>
        BehaviorTreeLayout.Build(new Delay(seconds).Build());

    private static NodeState Tick(
        ref FixedBehaviorTreeInstance<Nodes16, Bytes128> instance, Blackboard bb, float deltaTime)
    {
        bb.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
        return instance.Tick(bb);
    }

    [Test]
    public async Task The_Instance_Is_Unmanaged()
    {
        await Assert.That(
                RuntimeHelpers.IsReferenceOrContainsReferences<FixedBehaviorTreeInstance<Nodes16, Bytes128>>())
            .IsFalse();
    }

    [Test]
    public async Task Ticks_Identically_To_The_Class_Instance()
    {
        using BehaviorTree tree = new Selector(
            new Sequence(new Delay(0.5f), new Success()),
            new Failure()).Build();
        BehaviorTreeInstance<Blackboard> managed = tree.CreateInstance(new Blackboard());

        using BehaviorTreeLayout layout = BehaviorTreeLayout.Build(new Selector(
            new Sequence(new Delay(0.5f), new Success()),
            new Failure()).Build());
        var unmanaged = default(FixedBehaviorTreeInstance<Nodes16, Bytes128>);
        unmanaged.Initialize(layout.Handle);
        var bb = new Blackboard();

        for (int i = 0; i < 20; i++)
        {
            NodeState expected = managed.Tick(0.2f);
            NodeState actual = Tick(ref unmanaged, bb, 0.2f);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    /// <summary>A struct assignment IS the snapshot memcpy: the copy resumes exactly where the
    /// original was, and the two diverge from there.</summary>
    [Test]
    public async Task An_Assignment_Copy_Resumes_Mid_Flight()
    {
        using BehaviorTreeLayout layout = DelayLayout(0.3f);
        var original = default(FixedBehaviorTreeInstance<Nodes16, Bytes128>);
        original.Initialize(layout.Handle);
        var bb = new Blackboard();

        Tick(ref original, bb, 0.25f);

        FixedBehaviorTreeInstance<Nodes16, Bytes128> copy = original;

        // 0.05s left on both; one step finishes the copy, and the original independently.
        await Assert.That(Tick(ref copy, bb, 0.1f)).IsEqualTo(NodeState.Success);
        await Assert.That(Tick(ref original, bb, 0.1f)).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task A_Tree_Bigger_Than_The_Capacity_Is_Refused()
    {
        using BehaviorTreeLayout layout = BehaviorTreeLayout.Build(new Sequence(
            new Success(), new Success(), new Success(), new Success(),
            new Success(), new Success(), new Success(), new Success(),
            new Success(), new Success(), new Success(), new Success(),
            new Success(), new Success(), new Success(), new Success()).Build());
        BehaviorTreeLayoutHandle handle = layout.Handle;

        await Assert.That(() =>
        {
            var instance = default(FixedBehaviorTreeInstance<Nodes16, Bytes128>);
            instance.Initialize(handle);
        }).Throws<ArgumentException>().WithMessageContaining("at most 16 nodes");
    }

    [Test]
    public async Task A_Zeroed_Instance_Reports_Uninitialized()
    {
        var instance = default(FixedBehaviorTreeInstance<Nodes16, Bytes128>);

        await Assert.That(instance.IsInitialized).IsFalse();
    }
}

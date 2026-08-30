using System.Runtime.CompilerServices;
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

/// <summary>A minimal tree TYPE, so the generator emits <c>CountTreeBlackboard</c> and the typed
/// chain can be exercised end to end: Compile → typed layout → FixedBehaviorTree → typed
/// tick.</summary>
public readonly struct CountTree : IBehaviorTreeBuilder
{
    public static BTreeNode Build() => new Repeat(3, new Success());
}

[InlineArray(8)]
public struct States8
{
    private NodeState _element0;
}

[InlineArray(64)]
public struct Data64
{
    private byte _element0;
}

[InlineArray(1)]
public struct States1
{
    private NodeState _element0;
}

[InlineArray(4)]
public struct Data4
{
    private byte _element0;
}

/// <summary>
/// The typed surface: <c>FixedBehaviorTree</c> is the one type holding a raw blob pointer
/// (written only by <c>Initialize</c>, whose signature proves the tree matches), and the claims
/// that need pinning are the capacity refusal, that it ticks like any other instance form, and
/// that a struct assignment copies the tree mid-flight — what "can ride a snapshot memcpy" means.
/// </summary>
public sealed class FixedBehaviorTreeTests
{
    [Test]
    public async Task Typed_Chain_Ticks_End_To_End()
    {
        using BehaviorTreeLayout<CountTree> layout = BehaviorTrees.Compile<CountTree>();

        var instance = default(FixedBehaviorTree<CountTree, States8, Data64>);
        await Assert.That(instance.IsInitialized).IsFalse();

        instance.Initialize(layout);
        await Assert.That(instance.IsInitialized).IsTrue();

        // Repeat(3, Success): two ticks running, the third completes.
        await Assert.That(instance.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Success);

        // A finished tree restarts on the next tick.
        await Assert.That(instance.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Running);
    }

    [Test]
    public async Task An_Assignment_Copy_Resumes_Mid_Flight()
    {
        using BehaviorTreeLayout<CountTree> layout = BehaviorTrees.Compile<CountTree>();

        var original = default(FixedBehaviorTree<CountTree, States8, Data64>);
        original.Initialize(layout);
        original.Tick(CountTreeBlackboard.Bind());

        // The instance is BYTES: assignment is the snapshot memcpy in miniature, and the copy
        // resumes exactly where the original was — one repetition done, two to go.
        FixedBehaviorTree<CountTree, States8, Data64> copy = original;
        await Assert.That(copy.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Running);
        await Assert.That(copy.Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Success);
    }

    [Test]
    public async Task Initialize_Refuses_A_Tree_Bigger_Than_The_Buffers()
    {
        using BehaviorTreeLayout<CountTree> layout = BehaviorTrees.Compile<CountTree>();

        await Assert.That(() =>
        {
            var tiny = default(FixedBehaviorTree<CountTree, States1, Data4>);
            tiny.Initialize(layout);
        }).Throws<ArgumentException>().WithMessageContaining("holds at most");
    }

    [Test]
    public async Task A_Typed_Layout_Ref_Ticks_Over_Caller_Buffers()
    {
        using BehaviorTreeLayout<CountTree> layout = BehaviorTrees.Compile<CountTree>();
        var states = new NodeState[layout.Untyped.Blob.Count];
        var data = new byte[layout.Untyped.Blob.DataSize];
        layout.Ref(states, data).Untyped.ResetRuntimeData(0, layout.Untyped.Blob.Count);

        await Assert.That(layout.Ref(states, data).Tick(CountTreeBlackboard.Bind())).IsEqualTo(NodeState.Running);
        await Assert.That(layout.Ref(states, data).Status).IsEqualTo(NodeState.Running);
        layout.Ref(states, data).Reset(CountTreeBlackboard.Bind());
        await Assert.That(layout.Ref(states, data).Status).IsEqualTo((NodeState)0);
    }
}

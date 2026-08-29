using System.Runtime.CompilerServices;
using Paradise.BT.Nodes;
using Paradise.BT.Nodes.Builder;

namespace Paradise.BT.Test;

/// <summary>
/// The layout's native memory is freed by a finalizer and an instance holds a raw pointer into
/// it, so an instance created through <c>CreateInstance</c> must ROOT its owner — otherwise
/// `tree.CreateInstance(...)`, drop the tree, `Tick()` is a use-after-free that only shows in
/// Release, after a collection.
/// </summary>
public sealed class InstanceLifetimeTests
{
    [Test]
    public async Task An_Instance_Keeps_Its_Tree_Alive_Across_A_Collection()
    {
        WeakReference tree = CreateInstanceAndDropTree(out BehaviorTreeInstance<Blackboard> instance);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The tree (and through it the layout's native block) must survive for as long as the
        // instance does — without the rooting, this collection frees the blob under it.
        await Assert.That(tree.IsAlive).IsTrue();
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
        GC.KeepAlive(instance);
    }

    [Test]
    public async Task A_Layout_Instance_Keeps_Its_Layout_Alive_Across_A_Collection()
    {
        WeakReference layout = CreateInstanceAndDropLayout(out BehaviorTreeInstance instance);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Assert.That(layout.IsAlive).IsTrue();
        await Assert.That(instance.Tick(new Blackboard())).IsEqualTo(NodeState.Success);
        GC.KeepAlive(instance);
    }

    /// <summary>NoInlining, so the tree local is genuinely dead when the caller collects.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateInstanceAndDropTree(out BehaviorTreeInstance<Blackboard> instance)
    {
        BehaviorTree tree = new Success().Build();
        instance = tree.CreateInstance(new Blackboard());
        return new WeakReference(tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateInstanceAndDropLayout(out BehaviorTreeInstance instance)
    {
        BehaviorTreeLayout layout = BehaviorTreeLayout.Build(new Success().Build());
        instance = layout.CreateInstance();
        return new WeakReference(layout);
    }

    /// <summary>The lazy layout is built under a lock: a lost race would finalizer-free the loser
    /// while an instance may already hold its handle.</summary>
    [Test]
    public async Task Concurrent_First_Instances_Share_One_Layout()
    {
        using BehaviorTree tree = new Success().Build();

        BehaviorTreeInstance[] instances = new BehaviorTreeInstance[8];
        System.Threading.Tasks.Parallel.For(0, instances.Length, i => instances[i] = tree.CreateInstance());

        foreach (BehaviorTreeInstance instance in instances)
        {
            await Assert.That(instance.Tick(new Blackboard())).IsEqualTo(NodeState.Success);
        }
    }
}

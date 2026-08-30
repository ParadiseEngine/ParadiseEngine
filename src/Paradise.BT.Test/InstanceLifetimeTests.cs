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

    /// <summary>NoInlining, so the layout local is genuinely dead when the caller collects.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateInstanceAndDropLayout(out BehaviorTreeInstance instance)
    {
        BehaviorTreeLayout layout = new Success().Build();
        instance = layout.CreateInstance();
        return new WeakReference(layout);
    }

}

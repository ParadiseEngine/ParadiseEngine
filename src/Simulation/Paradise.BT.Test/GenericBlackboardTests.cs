using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;
namespace Paradise.BT.Test;

public sealed class GenericBlackboardTests
{
    private struct CountingBlackboard : IBlackboard
    {
        private Blackboard _inner;
        public int SetDataCount;

        public bool HasData<T>() where T : struct => _inner.HasData<T>();
        public T GetData<T>() where T : struct => _inner.GetData<T>();
        public void SetData<T>(T value) where T : struct
        {
            SetDataCount++;
            _inner.SetData(value);
        }
    }

    [Test]
    public async Task Generic_CreateInstance_Exposes_Custom_Blackboard_By_Ref()
    {
        var tree = BTreeNode.Build(new Success());
        var instance = tree.CreateInstance(new CountingBlackboard());

        // Caller writes persist through the ref exposed by the instance.
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(0);
        instance.Blackboard.SetData(42);
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(1);
        await Assert.That(instance.Blackboard.GetData<int>()).IsEqualTo(42);

        // Tick does not write to the blackboard — counter must stay put.
        instance.Tick();
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(1);
    }

    [Test]
    public async Task Generic_CreateInstance_Runs_Tree_To_Completion_With_Custom_Blackboard()
    {
        var tree = BTreeNode.Build(
            new Sequence(
                new Repeat(2, new Success()),
                new Success()));

        var instance = tree.CreateInstance(new CountingBlackboard());

        // Tick 1: Repeat has one completion of two -> Running. Tick 2: Repeat completes and the
        // sequence advances through its last child in the same tick -> Success.
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

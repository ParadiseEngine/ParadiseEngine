namespace Paradise.BT.Test;

public sealed class GenericBlackboardTests
{
    private struct CountingBlackboard : IMutableBlackboard
    {
        private Blackboard _inner;
        public int SetDataCount;

        public bool HasData<T>() where T : struct => _inner.HasData<T>();
        public T GetData<T>() where T : struct => _inner.GetData<T>();
        public ref T GetDataRef<T>() where T : struct => ref _inner.GetDataRef<T>();
        public bool HasData(Type type) => _inner.HasData(type);
        public IntPtr GetDataPtrRO(Type type) => _inner.GetDataPtrRO(type);
        public IntPtr GetDataPtrRW(Type type) => _inner.GetDataPtrRW(type);
        public T GetObject<T>() where T : class => _inner.GetObject<T>();

        public void SetData<T>(T value) where T : struct
        {
            SetDataCount++;
            _inner.SetData(value);
        }
    }

    [Test]
    public async Task Generic_CreateInstance_Uses_Custom_Blackboard_SetData_On_Tick()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Success());
        var instance = tree.CreateInstance(new CountingBlackboard());

        // Tick must route BehaviorTreeTickDeltaTime through the custom SetData.
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(0);
        instance.Tick(0.016f);
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(1);

        int countBeforeReset = instance.Blackboard.SetDataCount;
        instance.Reset();
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(countBeforeReset);

        instance.Tick(0.032f);
        await Assert.That(instance.Blackboard.SetDataCount).IsEqualTo(countBeforeReset + 1);
        await Assert.That(instance.Blackboard.GetData<BehaviorTreeTickDeltaTime>().Value).IsEqualTo(0.032f);
    }

    [Test]
    public async Task Generic_CreateInstance_Runs_Tree_To_Completion_With_Custom_Blackboard()
    {
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                BuiltInBehaviorNodes.Delay(0.5f),
                BuiltInBehaviorNodes.Success()));

        var instance = tree.CreateInstance(new CountingBlackboard());

        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Running);
        await Assert.That(instance.Tick(0.2f)).IsEqualTo(NodeState.Success);
    }
}

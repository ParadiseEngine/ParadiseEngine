namespace Paradise.BT.Test;

public sealed class GenericBlackboardTests
{
    private struct CountingBlackboard : IBlackboard
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
    public async Task Generic_CreateInstance_Exposes_Custom_Blackboard_By_Ref()
    {
        var tree = BehaviorTreeBuilder.Build(BuiltInBehaviorNodes.Success());
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
        var tree = BehaviorTreeBuilder.Build(
            BuiltInBehaviorNodes.Sequence(
                BuiltInBehaviorNodes.Delay(0.5f),
                BuiltInBehaviorNodes.Success()));

        var instance = tree.CreateInstance(new CountingBlackboard());

        // Caller writes delta time before each tick — the library does not.
        instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.2f));
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.2f));
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Running);
        instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.2f));
        await Assert.That(instance.Tick()).IsEqualTo(NodeState.Success);
    }
}

namespace Paradise.BT;

/// <summary>
/// A writable <see cref="IBlackboard"/>. Node types and the runtime that need to push typed struct values
/// into the blackboard (e.g. <see cref="BehaviorTreeTickDeltaTime"/> each tick) require this extended contract.
/// <see cref="IBlackboard"/> itself remains a read-oriented observer contract so consumers with read-only
/// blackboard implementations are unaffected.
/// </summary>
public interface IMutableBlackboard : IBlackboard
{
    void SetData<T>(T value) where T : struct;
}

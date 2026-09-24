namespace Paradise.BT;

public interface IBlackboard
{
    bool HasData<T>() where T : struct;
    T GetData<T>() where T : struct;
    void SetData<T>(T value) where T : struct;
}

/// <summary>Marks a generated blackboard for TTree so typed instances reject other trees' blackboards.</summary>
public interface IBlackboardFor<TTree> : IBlackboard;

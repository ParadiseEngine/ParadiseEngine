namespace Paradise.BT;

public interface IBlackboard
{
    bool HasData<T>() where T : struct;
    T GetData<T>() where T : struct;
    void SetData<T>(T value) where T : struct;
}

/// <summary>
/// A blackboard carrying exactly what the nodes of <typeparamref name="TTree"/> touch — stamped
/// by the binding generator on each generated blackboard. The phantom is what lets a typed layout
/// or ref refuse the wrong tree's blackboard at compile time.
/// </summary>
public interface IBlackboardFor<TTree> : IBlackboard;

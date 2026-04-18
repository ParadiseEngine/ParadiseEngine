namespace Paradise.BT;

/// <summary>
/// Exact blackboard contract used by EntitiesBT-style nodes.
/// </summary>
public interface IBlackboard
{
    bool HasData<T>() where T : struct;

    T GetData<T>() where T : struct;

    ref T GetDataRef<T>() where T : struct;

    bool HasData(Type type);

    IntPtr GetDataPtrRO(Type type);

    IntPtr GetDataPtrRW(Type type);

    T GetObject<T>() where T : class;
}

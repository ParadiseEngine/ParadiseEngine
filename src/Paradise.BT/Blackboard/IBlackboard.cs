namespace Paradise.BT;

public interface IBlackboard
{
    bool HasData<T>() where T : struct;
    T GetData<T>() where T : struct;
    void SetData<T>(T value) where T : struct;
}

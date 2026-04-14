using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Exact node contract used by EntitiesBT runtime nodes.
/// </summary>
public interface INodeData
{
    NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard;
}

/// <summary>
/// Optional reset callback used by nodes with custom reset behavior.
/// </summary>
public interface ICustomResetAction
{
    void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard;
}

internal interface IRuntimeNode
{
    int TypeId { get; }

    Type NodeType { get; }

    void CopyDefaultToRuntime();

    NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard;

    void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard;
}

internal interface IRuntimeNodeDataAccess
{
    ref T GetRuntimeData<T>() where T : struct;

    ref T GetDefaultData<T>() where T : struct;
}

internal sealed class RuntimeNode<TNodeData> : IRuntimeNode
    , IRuntimeNodeDataAccess
    where TNodeData : struct, INodeData
{
    private TNodeData _defaultData;
    private TNodeData _runtimeData;
    private readonly int _typeId;

    public RuntimeNode(TNodeData defaultData, int typeId)
    {
        _defaultData = defaultData;
        _runtimeData = defaultData;
        _typeId = typeId;
    }

    public int TypeId => _typeId;

    public Type NodeType => typeof(TNodeData);

    public ref TNodeData RuntimeData => ref _runtimeData;

    public ref TNodeData DefaultData => ref _defaultData;

    public void CopyDefaultToRuntime() => _runtimeData = _defaultData;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        ref TNodeData runtimeData = ref _runtimeData;
        return runtimeData.Tick(index, ref blob, ref bb);
    }

    public void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        if (_runtimeData is ICustomResetAction resetAction)
        {
            resetAction.Reset(index, ref blob, ref bb);
            _runtimeData = (TNodeData)resetAction;
        }
    }

    ref T IRuntimeNodeDataAccess.GetRuntimeData<T>()
    {
        if (typeof(T) != typeof(TNodeData))
        {
            throw new InvalidOperationException($"Runtime node data '{typeof(T).FullName}' does not match '{typeof(TNodeData).FullName}'.");
        }

        return ref Unsafe.As<TNodeData, T>(ref _runtimeData);
    }

    ref T IRuntimeNodeDataAccess.GetDefaultData<T>()
    {
        if (typeof(T) != typeof(TNodeData))
        {
            throw new InvalidOperationException($"Default node data '{typeof(T).FullName}' does not match '{typeof(TNodeData).FullName}'.");
        }

        return ref Unsafe.As<TNodeData, T>(ref _defaultData);
    }
}

using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Exact node contract used by EntitiesBT runtime nodes.
/// </summary>
public interface INodeData
{
    NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;

    /// <summary>
    /// Called when this node is reset, before it next ticks. Does nothing unless a node says
    /// otherwise — most do not, because restoring a node's DATA is not its job: the VM copies the
    /// authored default back over the runtime copy separately. This is the hook for a reset that
    /// has to touch something else, such as a counter on the blackboard.
    ///
    /// <b>STATIC, and that is a performance fix rather than a style.</b> It was an instance
    /// method with a default interface implementation, and a DIM invoked through a constrained
    /// type parameter BOXES THE RECEIVER — the runtime has no struct-specific implementation to
    /// call, so it boxes and dispatches to the interface's. Every node that did not override
    /// Reset — which was every shipped node — therefore allocated once per node per reset, and
    /// <see cref="VirtualMachine"/> resets a whole subtree at a time. A static member has no
    /// receiver, so there is nothing to box: the call resolves through the type parameter at JIT
    /// time.
    ///
    /// The cost of static is that a Reset cannot read or write the node's own fields. Nothing
    /// wanted to: the two implementations that existed touched only the blackboard.
    /// </summary>
    static virtual void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
    }
}

internal interface IRuntimeNode
{
    int TypeId { get; }

    Type NodeType { get; }

    void CopyDefaultToRuntime();

    NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;

    void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
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
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        ref TNodeData runtimeData = ref _runtimeData;
        return runtimeData.Tick(index, ref blob, ref bb);
    }

    /// <summary>Through the TYPE, not through the instance — <c>Reset</c> is a static interface
    /// member now, so there is no receiver to box. See <see cref="INodeData"/>.</summary>
    public void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => TNodeData.Reset(index, ref blob, ref bb);

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

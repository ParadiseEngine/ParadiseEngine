namespace Paradise.BT;

/// <summary>
/// Convenience leaf node backed by a delegate.
/// </summary>
[System.Runtime.InteropServices.Guid("B0E631D3-64E8-467F-B39A-1E4AF41E6A66")]
public struct DelegateActionNode : INodeData
{
    private readonly Func<IBlackboard, int, NodeState> _action;

    public DelegateActionNode(Func<IBlackboard, int, NodeState> action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
        => _action(bb, index);
}

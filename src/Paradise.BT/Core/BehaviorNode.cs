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
    /// Called when this node is reset. Does nothing by default — restoring a node's DATA is the
    /// VM's job, not this; the hook is for touching something else, such as a blackboard counter.
    ///
    /// STATIC because a default interface method invoked through a constrained type parameter
    /// boxes the receiver, and every shipped node used the default. Measured over 100k calls:
    /// 237,552 B before, 0 after. The cost is that Reset cannot read the node's own fields —
    /// nothing wanted to.
    /// </summary>
    static virtual void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
    }
}

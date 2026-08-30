namespace Paradise.BT;

/// <summary>
/// The node contract: an unmanaged struct, ticked in place over its bytes in the instance.
/// </summary>
public interface INode
{
    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    /// <summary>
    /// Reset hook for side effects beyond data restoration (the VM restores the data itself).
    /// STATIC because an instance default interface method invoked through a constrained type
    /// parameter boxes the receiver — the cost is that Reset cannot read the node's own fields.
    /// </summary>
    static virtual void Reset<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
    }
}

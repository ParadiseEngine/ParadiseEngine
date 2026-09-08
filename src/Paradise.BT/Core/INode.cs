namespace Paradise.BT;

/// <summary>
/// The node contract: an unmanaged struct, ticked in place over its bytes in the instance.
/// </summary>
public interface INode
{
    NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    /// <summary>Resets side effects after the VM restores node data.</summary>
    /// <remarks>The static hook avoids boxing and cannot read instance fields.</remarks>
    static virtual void Reset<TBehaviorTree, TBlackboard>(int index, TBehaviorTree tree, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
    }
}

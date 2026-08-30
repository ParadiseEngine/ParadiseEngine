namespace Paradise.BT;

[Flags]
public enum NodeState
{
    None = 0,
    Success = 1 << 0,
    Failure = 1 << 1,
    Running = 1 << 2,
}

public static class NodeStateExtensions
{
    public static bool HasFlagFast(this NodeState flags, NodeState flag)
        => (flags & flag) == flag;

    public static bool IsCompleted(this NodeState state)
        => state == NodeState.Success || state == NodeState.Failure;

    public static bool IsRunningOrFailure(this NodeState state)
        => state == NodeState.Failure || state == NodeState.Running;

    public static bool IsRunningOrSuccess(this NodeState state)
        => state == NodeState.Success || state == NodeState.Running;

    public static NodeState ToNodeState(this bool isSuccess)
        => isSuccess ? NodeState.Success : NodeState.Failure;
}

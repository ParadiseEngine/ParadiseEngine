using Paradise.BT;
using Paradise.BT.Builder;

public sealed class Delay : BTreeNode
{
    private readonly float _seconds;

    public Delay(float seconds) => _seconds = seconds;

    protected override BehaviorNodeDefinition ToDefinition()
        => BehaviorNodes.Node(new DelayTimerNode { TimerSeconds = _seconds });
}

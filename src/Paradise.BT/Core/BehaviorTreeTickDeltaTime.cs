namespace Paradise.BT;

/// <summary>
/// Default delta-time payload stored in the blackboard before each tick.
/// </summary>
public struct BehaviorTreeTickDeltaTime
{
    public BehaviorTreeTickDeltaTime(float value)
    {
        Value = value;
    }

    public float Value;
}

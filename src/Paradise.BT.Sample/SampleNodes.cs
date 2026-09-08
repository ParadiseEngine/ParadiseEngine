using System.Runtime.InteropServices;
using Paradise.BT;

// Sample-owned unmanaged nodes, clock data and generated builders.

/// <summary>Succeeds while the blackboard says there is a target.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D10")]
[Builder]
public struct HasTargetNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => bb.GetData<HasTargetData>().Value.ToNodeState();
}

/// <summary>Counts a shot onto the blackboard and says so.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D11")]
[Builder]
public struct FireShotNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        var shots = bb.GetData<ShotsFiredData>();
        shots = shots with { Value = shots.Value + 1 };
        bb.SetData(shots);
        Console.WriteLine($"Fired shot #{shots.Value}");
        return NodeState.Success;
    }
}

/// <summary>The fallback branch: do nothing, successfully.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D12")]
[Builder]
public struct IdleNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        Console.WriteLine("Idling...");
        return NodeState.Success;
    }
}

public struct HasTargetData
{
    public bool Value;
}

public struct ShotsFiredData
{
    public int Value;
}

namespace Paradise.BT.Sample
{
    /// <summary>The sample's own clock data — the library has no time concept. The caller writes
    /// it before each tick; DelayNode consumes it.</summary>
    public struct TickDeltaTime(float value)
    {
        public float Value = value;
    }

    /// <summary>Returns Running until TimerSeconds reaches zero.</summary>
    /// <remarks>Ticks mutate instance bytes; resetting restores the authored duration.</remarks>
    [Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D13")]
    [Reads<TickDeltaTime>]
    [Builder("Delay")]
    public struct DelayNode(float timerSeconds) : INode
    {
        public float TimerSeconds = timerSeconds;

        public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
            where TBehaviorTree : struct, IBehaviorTree, allows ref struct
            where TBlackboard : struct, IBlackboard, allows ref struct
        {
            TimerSeconds -= bb.GetData<TickDeltaTime>().Value;
            return TimerSeconds <= 0f ? NodeState.Success : NodeState.Running;
        }
    }
}

/// <summary>Implements a manual blackboard whose value copies share dictionary storage.</summary>
public struct Blackboard : IBlackboard
{
    private Dictionary<Type, object>? _data;

    private Dictionary<Type, object> Data => _data ??= new Dictionary<Type, object>();

    public bool HasData<T>() where T : struct => Data.ContainsKey(typeof(T));

    public T GetData<T>() where T : struct => (T)Data[typeof(T)];

    public void SetData<T>(T value) where T : struct => Data[typeof(T)] = value;
}

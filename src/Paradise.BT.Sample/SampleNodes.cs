using System.Runtime.InteropServices;
using Paradise.BT;

// The sample's own nodes. They are ordinary unmanaged structs implementing INodeData -- which is
// what every node in this library is now. The delegate-backed helpers this sample used to lean on
// (RunAction, CheckCondition wrapping DelegateActionNode/DelegateConditionNode) are gone: a node
// holding a Func cannot be stored as bytes, so it could never live in an unmanaged blob.
//
// [Builder] gives each one a generated builder class, so the DSL below still reads the same.

/// <summary>Succeeds while the blackboard says there is a target.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D10")]
[Builder]
public struct HasTargetNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => bb.GetData<HasTargetData>().Value.ToNodeState();
}

/// <summary>Counts a shot onto the blackboard and says so.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D11")]
[Builder]
public struct FireShotNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
    {
        ref ShotsFiredData shots = ref bb.GetDataRef<ShotsFiredData>();
        shots.Value++;
        Console.WriteLine($"Fired shot #{shots.Value}");
        return NodeState.Success;
    }
}

/// <summary>The fallback branch: do nothing, successfully.</summary>
[Guid("3F5A1C08-2B44-4E9A-9D71-6C0E8A2B4D12")]
[Builder]
public struct IdleNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
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

using System.Runtime.InteropServices;
using Paradise.BT.Nodes;
using Paradise.ECS;

namespace Paradise.BT.Sample;

// A deliberately busy tree, here to exercise the GENERATED blackboard rather than to be a tidy
// example. Nine node types over five data types, with the overlaps a one-node tree cannot
// produce: a component three separate nodes read, an extra four separate nodes write, a type both
// read and written, a node that touches nothing at all, and a node no line of this file names.
//
// The point of running it is that everything below is checked twice over. PBT0005 refuses a
// component ForagerRow does not claim; PBT0008 refuses a node trying to write one; PBT0009 refuses
// a node whose body reaches for something it did not declare. And because the sample publishes
// AOT, the emitted blackboard is proven to survive trimming and native compilation — which is
// where generated code usually breaks first.

// ===================== the world's vocabulary =====================

/// <summary>Where the forager is. Read by three different nodes, and must appear exactly ONCE in
/// the generated blackboard.</summary>
[Component]
public partial struct Position
{
    public float X;
}

/// <summary>How much energy is left. Read by two nodes; nothing writes it, because a node cannot
/// write a component (PBT0008) — the tree concludes, the caller applies.</summary>
[Component]
public partial struct Stamina
{
    public float Value;
}

/// <summary>What the forager can see. Claimed read-only and read by the two conditions.</summary>
[Component]
public partial struct Senses
{
    public bool ThreatNear;
    public float FoodX;
    public bool FoodVisible;
}

/// <summary>
/// The row the tree runs over. The claims here are the contract the whole tree is checked
/// against: a node reaching for anything not listed stops the build.
/// </summary>
[Queryable]
[With<Position>(IsReadOnly = true)]
[With<Stamina>(IsReadOnly = true)]
[With<Senses>(IsReadOnly = true)]
public readonly ref partial struct ForagerRow;

// ===================== what is not a component =====================

/// <summary>
/// The tree's conclusion. Not a component, so it lands in the generated Extras and the caller
/// reads it back — which is exactly how a node "writes" anything.
/// </summary>
public struct Intent
{
    public float GoalX;
    public bool HasGoal;
    public IntentKind Kind;
}

public enum IntentKind : byte
{
    Idle = 0,
    Flee = 1,
    Forage = 2,
    Rest = 3,
}

/// <summary>A running tally, so the sample can show that four different nodes wrote through one
/// generated field.</summary>
public struct Decisions
{
    public int Count;
}

// ===================== conditions =====================

/// <summary>Reads two things at once, which is the case a single-access node never covers.</summary>
[Guid("A0000000-0000-4000-8000-000000000001")]
[Builder]
[Reads<Senses>]
[Reads<Stamina>]
public struct ThreatNearNode : INodeData
{
    public float PanicStamina;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => (bb.GetData<Senses>().ThreatNear && bb.GetData<Stamina>().Value > PanicStamina)
            .ToNodeState();
}

/// <summary>The second reader of <see cref="Senses"/>: one field in the blackboard, two nodes.</summary>
[Guid("A0000000-0000-4000-8000-000000000002")]
[Builder]
[Reads<Senses>]
public struct FoodVisibleNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => bb.GetData<Senses>().FoodVisible.ToNodeState();
}

/// <summary>The second reader of <see cref="Stamina"/>, and a node that reads a component while
/// writing an extra — the mixed case.</summary>
[Guid("A0000000-0000-4000-8000-000000000003")]
[Builder]
[Reads<Stamina>]
public struct ExhaustedNode : INodeData
{
    public float RestBelow;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => (bb.GetData<Stamina>().Value < RestBelow).ToNodeState();
}

/// <summary>Reads what an earlier branch concluded: an extra that is READ as well as written, so
/// the generator's merge has to keep it a write rather than demote it.</summary>
[Guid("A0000000-0000-4000-8000-000000000004")]
[Builder]
[Reads<Intent>]
public struct AlreadyDecidedNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => bb.GetData<Intent>().HasGoal.ToNodeState();
}

// ===================== actions =====================

/// <summary>Runs away: reads the pose it is fleeing FROM and writes where to go.</summary>
[Guid("A0000000-0000-4000-8000-000000000005")]
[Builder]
[Reads<Position>]
[Reads<Senses>]
[Writes<Intent>]
[Writes<Decisions>]
public struct FleeNode : INodeData
{
    public float Distance;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        float here = bb.GetData<Position>().X;
        float away = bb.GetData<Senses>().FoodX < here ? here + Distance : here - Distance;

        bb.SetData(bb.GetData<Intent>() with
        {
            GoalX = away,
            HasGoal = true,
            Kind = IntentKind.Flee,
        });
        bb.SetData(bb.GetData<Decisions>() with { Count = bb.GetData<Decisions>().Count + 1 });
        return NodeState.Success;
    }
}

/// <summary>The second reader of <see cref="Position"/> and the second writer of both extras.</summary>
[Guid("A0000000-0000-4000-8000-000000000006")]
[Builder]
[Reads<Position>]
[Reads<Senses>]
[Writes<Intent>]
[Writes<Decisions>]
public struct SeekFoodNode : INodeData
{
    public float ArriveWithin;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        float food = bb.GetData<Senses>().FoodX;
        bb.SetData(bb.GetData<Intent>() with
        {
            GoalX = food,
            HasGoal = true,
            Kind = IntentKind.Forage,
        });
        bb.SetData(bb.GetData<Decisions>() with { Count = bb.GetData<Decisions>().Count + 1 });

        return (MathF.Abs(food - bb.GetData<Position>().X) <= ArriveWithin).ToNodeState();
    }
}

/// <summary>The third reader of <see cref="Position"/>, and the branch that always succeeds so the
/// Selector always concludes something.</summary>
[Guid("A0000000-0000-4000-8000-000000000007")]
[Builder]
[Reads<Position>]
[Writes<Intent>]
[Writes<Decisions>]
public struct RestNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        bb.SetData(bb.GetData<Intent>() with
        {
            GoalX = bb.GetData<Position>().X,
            HasGoal = true,
            Kind = IntentKind.Rest,
        });
        bb.SetData(bb.GetData<Decisions>() with { Count = bb.GetData<Decisions>().Count + 1 });
        return NodeState.Success;
    }
}

/// <summary>The fourth writer of <see cref="Decisions"/>, and the only node that touches exactly
/// one thing.</summary>
[Guid("A0000000-0000-4000-8000-000000000008")]
[Builder]
[Writes<Decisions>]
public struct TallyNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        bb.SetData(bb.GetData<Decisions>() with { Count = bb.GetData<Decisions>().Count + 1 });
        return NodeState.Success;
    }
}

/// <summary>Touches nothing. Structure-only nodes are the normal case and must contribute no
/// field, which is what stops the blackboard growing with the tree.</summary>
[Guid("A0000000-0000-4000-8000-000000000009")]
[Builder]
public struct NoopNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Success;
}

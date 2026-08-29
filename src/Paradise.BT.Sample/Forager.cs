using System.Runtime.InteropServices;
using Paradise.BT.Nodes;
using Paradise.ECS;

namespace Paradise.BT.Sample;

// A deliberately busy tree, to exercise the GENERATED blackboard rather than to be a tidy example:
// ten node types over five data types, with the overlaps a small tree cannot produce — a component
// three nodes read, an extra four nodes write, a type both read and written, and a node touching
// nothing at all.
//
// NOT ONE NODE BELOW DECLARES ITS ACCESS. The generator reads each Tick body instead. The
// attributes still matter for a node in a REFERENCED assembly, where no body exists; the two are
// unioned.

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
public struct ThreatNearNode(float panicStamina) : INodeData
{
    public float PanicStamina = panicStamina;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => (bb.GetData<Senses>().ThreatNear && bb.GetData<Stamina>().Value > PanicStamina)
            .ToNodeState();
}

/// <summary>The second reader of <see cref="Senses"/>: one field in the blackboard, two nodes.</summary>
[Guid("A0000000-0000-4000-8000-000000000002")]
[Builder]
public struct FoodVisibleNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => bb.GetData<Senses>().FoodVisible.ToNodeState();
}

/// <summary>The second reader of <see cref="Stamina"/>: a pure condition, writing nothing.</summary>
[Guid("A0000000-0000-4000-8000-000000000003")]
[Builder]
public struct ExhaustedNode(float restBelow) : INodeData
{
    public float RestBelow = restBelow;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => (bb.GetData<Stamina>().Value < RestBelow).ToNodeState();
}

/// <summary>
/// Is the trip worth making? The only node here with more than one authored number, so it is what
/// exercises a builder constructor carrying several: <c>ForageWorthIt(0.3f, 6f, true)</c>.
///
/// The three would otherwise be three separate conditions sequenced together, which reads worse
/// and costs a node apiece in a component sized at compile time.
/// </summary>
[Guid("A0000000-0000-4000-8000-00000000000A")]
[Builder]
public struct ForageWorthItNode(float minStamina, float maxDistance, bool requireVisible) : INodeData
{
    /// <summary>Below this there is no point setting out.</summary>
    public float MinStamina = minStamina;

    /// <summary>Further than this and the walk costs more than the meal.</summary>
    public float MaxDistance = maxDistance;

    /// <summary>Whether food it cannot currently see counts. False lets it head for a remembered
    /// spot.</summary>
    public bool RequireVisible = requireVisible;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        Senses senses = bb.GetData<Senses>();
        if (RequireVisible && !senses.FoodVisible)
        {
            return NodeState.Failure;
        }

        return (bb.GetData<Stamina>().Value >= MinStamina
            && MathF.Abs(senses.FoodX - bb.GetData<Position>().X) <= MaxDistance)
            .ToNodeState();
    }
}

/// <summary>Reads what an earlier branch concluded: an extra that is READ as well as written, so
/// the generator's merge has to keep it a write rather than demote it.</summary>
[Guid("A0000000-0000-4000-8000-000000000004")]
[Builder]
public struct AlreadyDecidedNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => bb.GetData<Intent>().HasGoal.ToNodeState();
}

// ===================== actions =====================

/// <summary>Runs away: reads the pose it is fleeing FROM and writes where to go.</summary>
[Guid("A0000000-0000-4000-8000-000000000005")]
[Builder]
public struct FleeNode(float distance) : INodeData
{
    public float Distance = distance;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
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
public struct SeekFoodNode(float arriveWithin) : INodeData
{
    public float ArriveWithin = arriveWithin;

    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
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
public struct RestNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
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
public struct TallyNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(
        int index, TNodeBlob blob, TBlackboard bb)
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
        int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => NodeState.Success;
}

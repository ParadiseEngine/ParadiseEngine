using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT.Test;

/// <summary>
/// What a test observes about a tick: how many times each probe ran, and the delta time the last
/// one saw. It lives on the BLACKBOARD because a node's data is bytes — a node cannot close over a
/// test's local like the delegate nodes used to.
/// </summary>
public struct ProbeData
{
    public ProbeCounts Counts;
    public float LastDeltaTime;

    public readonly int Count(int slot) => Counts[slot];
}

[InlineArray(8)]
public struct ProbeCounts
{
    private int _element0;
}

/// <summary>How a <see cref="ProbeNode"/> decides what to return.</summary>
public enum ProbeRule : byte
{
    /// <summary>Always <see cref="ProbeNode.Result"/>.</summary>
    Always = 0,

    /// <summary><see cref="ProbeNode.After"/> once it has run at least Threshold times.</summary>
    CountAtLeast = 1,

    /// <summary><see cref="ProbeNode.After"/> on every even-numbered run.</summary>
    CountEven = 2,
}

/// <summary>
/// The replacement for the delegate-backed action node: it counts its own ticks into a blackboard
/// slot and returns a state chosen by a rule.
///
/// A struct with fields rather than a captured lambda, which is the whole point — every node type
/// is unmanaged now, so a node's data can live in a blob as bytes.
/// </summary>
[Guid("7C1B4E22-9A3D-4F51-8E60-1B2C3D4E5F60")]
public struct ProbeNode : INodeData
{
    public int Slot;
    public NodeState Result;
    public NodeState After;
    public ProbeRule Rule;
    public int Threshold;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        ref ProbeData probe = ref bb.GetDataRef<ProbeData>();
        int runs = ++probe.Counts[Slot];

        return Rule switch
        {
            ProbeRule.CountAtLeast => runs >= Threshold ? After : Result,
            ProbeRule.CountEven => runs % 2 == 0 ? After : Result,
            _ => Result,
        };
    }
}

/// <summary>Records the delta time the blackboard carried this tick, for the one test that is
/// about delta-time propagation.</summary>
[Guid("7C1B4E22-9A3D-4F51-8E60-1B2C3D4E5F61")]
public struct RecordDeltaTimeNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        bb.GetDataRef<ProbeData>().LastDeltaTime =
            bb.GetData<BehaviorTreeTickDeltaTime>().Value;
        return NodeState.Success;
    }
}

public static class TestBehaviorNodes
{
    /// <summary>A probe that counts into <paramref name="slot"/> and always returns
    /// <paramref name="result"/>.</summary>
    public static BehaviorNodeDefinition Probe(int slot = 0, NodeState result = NodeState.Success)
        => BehaviorNodes.Node(new ProbeNode { Slot = slot, Result = result });

    /// <summary>A probe that returns <paramref name="before"/> until it has run
    /// <paramref name="threshold"/> times, then <paramref name="after"/>.</summary>
    public static BehaviorNodeDefinition ProbeUntil(
        int threshold, NodeState before, NodeState after, int slot = 0)
        => BehaviorNodes.Node(new ProbeNode
        {
            Slot = slot,
            Rule = ProbeRule.CountAtLeast,
            Threshold = threshold,
            Result = before,
            After = after,
        });

    /// <summary>A probe that returns <paramref name="odd"/> on odd runs and
    /// <paramref name="even"/> on even ones.</summary>
    public static BehaviorNodeDefinition ProbeAlternating(
        NodeState odd, NodeState even, int slot = 0)
        => BehaviorNodes.Node(new ProbeNode
        {
            Slot = slot,
            Rule = ProbeRule.CountEven,
            Result = odd,
            After = even,
        });

    public static BehaviorNodeDefinition RecordDeltaTime()
        => BehaviorNodes.Node(new RecordDeltaTimeNode());

    /// <summary>A blackboard already carrying <see cref="ProbeData"/>, which every probe writes
    /// through.</summary>
    public static Blackboard NewBlackboard()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new ProbeData());
        return blackboard;
    }

    public static BehaviorTreeSerializationRegistry BuiltInRegistry()
        => BuiltInBehaviorNodes.CreateRegistry();
}

internal static class TestTickExtensions
{
    /// <summary>
    /// Test-only convenience: writes <see cref="BehaviorTreeTickDeltaTime"/> to the blackboard then ticks.
    /// The library intentionally does not expose this — delta-time propagation is a caller concern.
    /// </summary>
    public static NodeState Tick(this BehaviorTreeInstance<Blackboard> instance, float deltaTime)
    {
        instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(deltaTime));
        return instance.Tick();
    }

    /// <summary>How many times the probe in <paramref name="slot"/> has run.</summary>
    public static int ProbeCount(this BehaviorTreeInstance<Blackboard> instance, int slot = 0)
        => instance.Blackboard.GetData<ProbeData>().Count(slot);
}

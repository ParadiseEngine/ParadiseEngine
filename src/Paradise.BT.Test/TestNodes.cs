using Paradise.BT.Builder;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.BT.Test;

/// <summary>
/// What a test observes about a tick: how many times each probe ran. It lives on the BLACKBOARD because a node's data is bytes — a node cannot close over a
/// test's local like the delegate nodes used to.
/// </summary>
public struct ProbeData
{
    public ProbeCounts Counts;

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
public struct ProbeNode : INode
{
    public int Slot;
    public NodeState Result;
    public NodeState After;
    public ProbeRule Rule;
    public int Threshold;

    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        // Read-modify-write rather than `with`: the mutation is into an inline array, which a
        // `with` expression cannot address.
        var probe = bb.GetData<ProbeData>();
        int runs = ++probe.Counts[Slot];
        bb.SetData(probe);

        return Rule switch
        {
            ProbeRule.CountAtLeast => runs >= Threshold ? After : Result,
            ProbeRule.CountEven => runs % 2 == 0 ? After : Result,
            _ => Result,
        };
    }
}

public static class TestBehaviorNodes
{
    /// <summary>A probe that counts into <paramref name="slot"/> and always returns
    /// <paramref name="result"/>.</summary>
    public static BTreeNode Probe(int slot = 0, NodeState result = NodeState.Success)
        => new LeafNode<ProbeNode>(new ProbeNode { Slot = slot, Result = result });

    /// <summary>A probe that returns <paramref name="before"/> until it has run
    /// <paramref name="threshold"/> times, then <paramref name="after"/>.</summary>
    public static BTreeNode ProbeUntil(
        int threshold, NodeState before, NodeState after, int slot = 0)
        => new LeafNode<ProbeNode>(new ProbeNode
        {
            Slot = slot,
            Rule = ProbeRule.CountAtLeast,
            Threshold = threshold,
            Result = before,
            After = after,
        });

    /// <summary>A probe that returns <paramref name="odd"/> on odd runs and
    /// <paramref name="even"/> on even ones.</summary>
    public static BTreeNode ProbeAlternating(
        NodeState odd, NodeState even, int slot = 0)
        => new LeafNode<ProbeNode>(new ProbeNode
        {
            Slot = slot,
            Rule = ProbeRule.CountEven,
            Result = odd,
            After = even,
        });

    /// <summary>A blackboard already carrying <see cref="ProbeData"/>, which every probe writes
    /// through.</summary>
    public static Blackboard NewBlackboard()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new ProbeData());
        return blackboard;
    }

}

/// <summary>
/// Minimal handle-shaped <see cref="IBlackboard"/> for tests: a struct holding one class
/// reference, so the by-value copies the VM makes all write to the same storage. The library
/// deliberately ships no blackboard implementation.
/// </summary>
public struct Blackboard : IBlackboard
{
    private Dictionary<Type, object>? _data;

    private Dictionary<Type, object> Data => _data ??= new Dictionary<Type, object>();

    public bool HasData<T>() where T : struct => Data.ContainsKey(typeof(T));

    public T GetData<T>() where T : struct => (T)Data[typeof(T)];

    public void SetData<T>(T value) where T : struct => Data[typeof(T)] = value;
}

/// <summary>
/// Test-only pairing of a tree instance (layout + owned buffers) with the blackboard it ticks
/// against. The library deliberately has no owned-blackboard instance — the blackboard is passed
/// per call — but a test reads much better when the pair travels together.
/// </summary>
internal sealed class TestInstance<TBlackboard>
    where TBlackboard : struct, IBlackboard
{
    private readonly BehaviorTreeLayout _layout;
    private readonly NodeState[] _states;
    private readonly byte[] _data;
    private TBlackboard _blackboard;

    public TestInstance(BehaviorTreeLayout layout, TBlackboard blackboard)
    {
        _layout = layout;
        _states = new NodeState[layout.Blob.Count];
        _data = new byte[Math.Max(1, layout.Blob.DataSize)];
        _blackboard = blackboard;
        Reset();
    }

    public ref TBlackboard Blackboard => ref _blackboard;

    public NodeState Status => _states[0];

    public bool AutoResetOnCompletion { get; set; } = true;

    private BehaviorTreeRef Blob => new(ref _layout.Blob, _states, _data);

    public NodeState Tick()
    {
        if (AutoResetOnCompletion && Status.IsCompleted())
        {
            VirtualMachine.Reset(Blob, _blackboard);
        }

        return VirtualMachine.Tick(Blob, _blackboard);
    }

    public void Reset() => VirtualMachine.Reset(Blob, _blackboard);
}

internal static class TestTickExtensions
{
    /// <summary>The owned-blackboard, owned-buffer shape tests read best with, rebuilt over the
    /// public per-call API.</summary>
    public static TestInstance<TBlackboard> CreateInstance<TBlackboard>(
        this BehaviorTreeLayout layout, TBlackboard blackboard)
        where TBlackboard : struct, IBlackboard
        => new(layout, blackboard);

    /// <summary>How many times the probe in <paramref name="slot"/> has run.</summary>
    public static int ProbeCount(this TestInstance<Blackboard> instance, int slot = 0)
        => instance.Blackboard.GetData<ProbeData>().Count(slot);
}

/// <summary>
/// Test-side inspection of a compiled layout, through its internal handle (InternalsVisibleTo) —
/// the public surface is deliberately opaque.
/// </summary>
internal static class TreeTestExtensions
{
    public static Type GetNodeType(this BehaviorTreeLayout layout, int nodeIndex)
        => NodeTypeRegistry.Invoker(layout.Blob.TypeGuid(nodeIndex)).NodeType;

    public static int GetEndIndex(this BehaviorTreeLayout layout, int nodeIndex)
        => layout.Blob.EndIndices[nodeIndex];
}

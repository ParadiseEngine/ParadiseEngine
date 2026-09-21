# Paradise.BT

A .NET behavior tree framework with unmanaged struct nodes, one shared position-independent
blob per compiled tree, and two buffers per instance. Instances fit in ECS components and
support memcpy snapshots. NativeAOT and trimming compatible; ticking allocates nothing.

It has no Unity, Godot or ECS dependency. Inspired by
[EntitiesBT](https://github.com/quabug/EntitiesBT), it keeps the flat pre-order layout and
default/runtime data split, with static-generic dispatch replacing reflection.

## Packages

| package | what it holds |
|---|---|
| `Paradise.BT` | the runtime: node contract, layout, instances, virtual machine |
| `Paradise.BT.Builder` | authoring: `BTreeNode`, `IBehaviorTreeBuilder`, `BehaviorTrees` |
| `Paradise.BT.Nodes` | the built-in nodes |
| `Paradise.BT.Generators` | source generators + analyzers — **reference as an analyzer from every project that declares node types or trees** |
| `Paradise.BLOB` | the binary blob primitives the layout is built on (transitive) |

The generator reference is required: it registers node types through module initializers,
emits builders and per-tree blackboards, publishes access metadata, and enforces diagnostics.
Layout construction rejects unregistered node types by name.

## The model

Each tick returns a `NodeState`:

| state | meaning |
|---|---|
| `Success` / `Failure` | the node concluded |
| `Running` | not done — tick me again |
| `None` (0) | never ticked, or reset since — a real state, which decorators branch on to detect an exhausted child |

Three node kinds compose a tree: **leaves** act, **decorators** wrap one child, **composites**
route among many. The built-in composites give the classic semantics:

| builder | node | semantics |
|---|---|---|
| `Sequence(…)` | `SequenceNode` | tick children left to right; stop on `Running` or `Failure` |
| `Selector(…)` | `SelectorNode` | tick children left to right; stop on `Running` or `Success` |
| `Parallel(…)` | `ParallelNode` | tick every unfinished child; `Running` beats `Failure` beats `Success` |
| `Repeat(n, child, breakStates)` | `RepeatTimesNode` | re-run the child `n` completions; return early on a state in `breakStates` |
| `RepeatForever(breakStates, child)` | `RepeatForeverNode` | re-run the child until a state in `breakStates` |
| `Inverter(child)` | `InverterNode` | swap `Success` and `Failure` |
| `Succeeder(child)` | `SucceederNode` | any completion becomes `Success` |
| `Success()` / `Failure()` / `Running()` | — | constants, for shaping a tree |

Execution rules:

- **Completed children are skipped.** A sequence resumes at its first unfinished child.
- **Reset restores authored data.** Resetting a subtree clears its states and memcpys the
  authored defaults back over its runtime data — one copy, because a subtree is contiguous. A
  finished tree is restarted by its owner's next tick (`FixedBehaviorTree` does this
  automatically).

## Architecture

```
authoring                        compile                       runtime (per agent)
─────────                        ───────                       ───────────────────
BTreeNode graph      ──────▶     BehaviorTreeLayout            NodeState[ ]  (1 per node)
(generated builders, or          one shared native blob:       byte[ ]       (node data)
 raw LeafNode<T> et al.)         topology · GUID table +            │ both point into
                                 offsets · defaults                 ▼
                                        ▲                      BehaviorTree (ref struct view)
                                        └──────────────────────────┘
                                                              VirtualMachine.Tick
```

The layout is the tree's shared, immutable half: end indices (node *i*'s subtree is the
contiguous range `[i, End[i])`, so its first child is `i+1` and a sibling is one jump), a GUID
table with one entry per distinct node type plus each node's index into it — a node's type is its
GUID, the same identity at rest, in memory, and at dispatch — per-node data offsets packed at
each type's natural alignment (capped at the blob's 16-byte alignment), and the authored
defaults. A thousand agents share one layout; each owns only the two buffers. Dispatch goes
through a process-wide registry of static-generic invokers keyed by that GUID — no reflection,
no boxing, no lock on the tick path.

**Ownership:** whoever compiles a layout owns it (`IDisposable`, native memory) and must keep it
alive while instances tick against it.

## Writing a node

A node is an unmanaged struct implementing `INode`, identified by `[Guid]`. Its **primary
constructor is its exposed surface**: `[Builder]` makes the generator emit a builder class
mirroring those parameters — order and declared defaults included — and everything else in the
struct is runtime state the builder never shows.

```csharp
using Paradise.BT;
using System.Runtime.InteropServices;

[Guid("7D4E31B3-0D57-4211-9C1F-91EAB87734E5")]
[Builder]                                       // emits `Counter`; name defaults to the type minus "Node"
public struct CounterNode(int threshold) : INode
{
    public int Threshold = threshold;           // exposed: mirrored by the builder
    private int _count;                         // runtime state: invisible to the builder

    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        _count++;                               // persists: a node ticks THROUGH its bytes
        return _count >= Threshold ? NodeState.Success : NodeState.Running;
    }
}
```

- **Field writes persist** across ticks (the node is reinterpreted in place over the instance's
  bytes) and are undone by a reset, which restores the authored value.
- **`record struct`** works as the one-line form: `record struct Patrol(float Radius) : INode`.
- **Captured parameters** work too — `struct Cooldown(float seconds)` mutating `seconds` in
  `Tick` is the shortest possible stateful node.
- **Cardinality** is part of `[Builder]`: `[Builder(NodeCardinality.Decorator)]` gives the
  builder a `child` parameter, `Composite` a `params children` span, and the core builder
  validates the counts (a leaf with children or a decorator without exactly one is refused).
- **Optional `Reset` hook**: implement `static virtual void Reset<…>` for side effects beyond
  data restoration (it is static — receiverless — so it cannot read the node's own fields).
- Builder calls passing **two or more values must name them** (`PBT0013`); a single value or a
  child argument stays positional — `Counter(3)` and `Repeat(3, child)` are fine,
  `Forage(0.3f, 6f)` is not.

Registration is automatic: the generator emits one module initializer per assembly. Only a node
it cannot see — private, or declared in a project without the analyzer reference — needs an
explicit `NodeTypeRegistry.Register<T>()`.

## The blackboard

Nodes access external data through three methods. Without ref returns, generators can
distinguish reads from writes and derive the tree's data contract:

```csharp
public interface IBlackboard
{
    bool HasData<T>() where T : struct;
    T GetData<T>() where T : struct;
    void SetData<T>(T value) where T : struct;
}
```

Choose a manual or generated blackboard:

**A hand-written blackboard** is a handle struct implementing `IBlackboard`. The library
provides no implementation, clock or delta-time type; callers supply time through their own
data and nodes. A minimal blackboard holds a dictionary reference:

```csharp
public struct Blackboard : IBlackboard
{
    private Dictionary<Type, object>? _data;
    private Dictionary<Type, object> Data => _data ??= new();
    public bool HasData<T>() where T : struct => Data.ContainsKey(typeof(T));
    public T GetData<T>() where T : struct => (T)Data[typeof(T)];
    public void SetData<T>(T value) where T : struct => Data[typeof(T)] = value;
}

using BehaviorTreeLayout tree = new Selector(
    new Sequence(new Repeat(2, new Success()), new Success()),
    new Failure()
).Build();

var states = new NodeState[tree.Blob.Count];
var data = new byte[tree.Blob.DataSize];
var bb = new Blackboard();
NodeState state = VirtualMachine.Tick(
    new BehaviorTreeRef(ref tree.Blob, states, data), bb);   // the blackboard is passed per tick
```

The runtime never reads a clock, which is what keeps a run reproducible.

**The generated per-tree blackboard** is the zero-allocation path. A tree is a type implementing
`IBehaviorTreeBuilder` (or `IBehaviorTreeBuilder<TArgs>` when built from a config) — that
interface is the entire marker, and it is compile-checked: a tree type must have a `Build`
returning its root.

```csharp
using Paradise.BT.Builder;
using Paradise.BT.Nodes.Builder;

public readonly struct PatrolTree : IBehaviorTreeBuilder
{
    public static BTreeNode Build() =>
        new Sequence(
            new Counter(3),
            new Repeat(2, new Success()));
}

using BehaviorTreeLayout<PatrolTree> layout = BehaviorTrees.Compile<PatrolTree>();

// States16 / Bytes256: your own [InlineArray] buffer structs — their sizes are the capacity.
var agent = default(FixedBehaviorTree<PatrolTree, States16, Bytes256>);
agent.Initialize(layout);   // refuses another tree's layout — the layout is TYPED

NodeState state = agent.Tick(PatrolTreeBlackboard.Bind(/* one named ref per accessed type */));
```

The source generator emits a blackboard implementing `IBlackboardFor<PatrolTree>`; the typed
layout, `BehaviorTreeRef<PatrolTree>` and `FixedBehaviorTree` reject other trees' blackboards at
compile time. `BTreeNode.Build()` uses the untyped path without a generated blackboard.

The generator finds nodes through builders, factories returning builders, or explicit
`[BehaviorTreeBinding(Also = […])]` entries, reads their `GetData`/`SetData` calls, and emits a blackboard:
a `ref struct` holding `ref readonly` to everything the tree reads and `ref` to everything it
writes, directly accessing caller storage. **The union of node access defines the tree's
contract.** Removing the last node that reads a type removes it from the blackboard; mismatched
`Bind` calls then fail to compile.

`PatrolTreeBlackboard` above has no entries because its nodes access no external data. For
referenced nodes, the declaring assembly publishes body access as `[assembly: NodeAccess]`
metadata. Explicit `[Reads<T>]`/`[Writes<T>]` attributes can describe access hidden behind
helper calls (`PBT0010`).

Pass a generated blackboard **per `Tick` call** (it is a `ref struct`; no field can hold one),
and pass `Bind` arguments **by name** — parameters are ordered by type name, so adding a node
can reorder them.

## Using it inside an ECS

The framework takes no ECS dependency, but its shapes are ECS-shaped on purpose:

- An instance's state is a `NodeState` span plus a byte span — storable inline in a component
  via `FixedBehaviorTree<TTree, TStates, TData>`, copied by any world-snapshot memcpy, pointed
  at the shared layout through a stored `LayoutBlob` pointer.
- The raw path builds a `BehaviorTreeRef` over your own memory each tick and calls the VM.

```csharp
var blob = new BehaviorTreeRef(ref layout.Blob, statesSpan, dataSpan);
NodeState state = VirtualMachine.Tick(blob, blackboard);
```

- A type marked `[Component]` (or implementing an interface named `Paradise.ECS.IComponent` —
  recognized structurally, no reference taken) binds **read-only** in a generated blackboard,
  and a node that writes one is refused at compile time (`PBT0008`). The pattern that follows:
  a tree writes a *conclusion* — plain structs like an `Intent` — and the owning system applies
  it to the world. That is what lets one tree drive bodies steered any way you like.

## Diagnostics

All enforced at compile time by `Paradise.BT.Generators`:

| id | says |
|---|---|
| `PBT0001` | a `[Builder]` node is missing `[Guid]` |
| `PBT0002` | a `[Builder]` node contains managed references (warning) |
| `PBT0003` | two node types share a GUID |
| `PBT0008` | a node writes a component — components bind read-only by value; write a conclusion |
| `PBT0009` | a node's body touches something its declared access omits |
| `PBT0010` | a blackboard handed to a method the access scan cannot follow (warning) |
| `PBT0011` | a node declares more than one public constructor — the surface is ambiguous |
| `PBT0012` | a public field is not part of the node's constructor surface (warning) |
| `PBT0013` | a builder call passes several values positionally — name them |

## Design notes

- Unmanaged instances keep tree state, including timers, in simulation components and snapshots.
- The tick path allocates nothing: dispatch is a lock-free GUID lookup into static-generic invokers,
  and the generated blackboard's `GetData<T>`/`SetData<T>` fold to direct field access at JIT
  time.
- Determinism is a design goal throughout: no clocks and no reflection at tick time.

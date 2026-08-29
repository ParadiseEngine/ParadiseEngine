# Paradise.BT

A generic behavior tree framework for .NET. Nodes are unmanaged structs, a compiled tree is one
shared, position-independent binary blob, and a running instance is two plain buffers — small
enough to live in an ECS component, dumb enough to survive a memcpy, and deterministic enough to
ride a world snapshot. NativeAOT and trimming compatible; zero allocation on the tick path.

It is engine-agnostic: no Unity, no Godot, no ECS dependency. Inspired by
[EntitiesBT](https://github.com/quabug/EntitiesBT), keeping its best mechanics (the flat
pre-order layout, the default/runtime data split) and replacing its reflection-built dispatch
with static generics.

## Packages

| package | what it holds |
|---|---|
| `Paradise.BT` | the runtime: node contract, layout, instances, virtual machine, serialization |
| `Paradise.BT.Builder` | authoring: `BTreeNode`, `IBehaviorTreeBuilder`, `BehaviorTrees` |
| `Paradise.BT.Nodes` | the built-in nodes and a reference dictionary `Blackboard` |
| `Paradise.BT.Generators` | source generators + analyzers — **reference as an analyzer from every project that declares node types or trees** |
| `Paradise.BLOB` | the binary blob primitives the layout is built on (transitive) |

The generator reference is load-bearing, not optional tooling: it registers every node type via a
module initializer, emits the builder classes and per-tree blackboards, publishes access
metadata, and enforces the diagnostics below. A node type it cannot see is refused by name when a
layout is built from it.

## The model

A behavior tree is ticked once per frame (or however often you like) and every node answers with
a `NodeState`:

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
| `Delay(seconds)` | `DelayTimerNode` | `Running` until the accumulated delta time elapses |
| `Success()` / `Failure()` / `Running()` | — | constants, for shaping a tree |

Two semantics worth knowing before writing a tree:

- **Completed children are not re-ticked.** A composite skips a child already at
  `Success`/`Failure`, which is what gives a sequence resume behavior across frames — a
  three-step sequence whose second step is `Running` picks up at step two next tick.
- **Reset restores authored data.** Resetting a subtree clears its states and memcpys the
  authored defaults back over its runtime data — one copy, because a subtree is contiguous. A
  finished `BehaviorTreeInstance` resets itself on the next tick by default
  (`AutoResetOnCompletion`).

## Architecture

```
authoring                    compile                        runtime (per agent)
─────────                    ───────                        ───────────────────
BTreeNode graph   ──────▶    BehaviorTree     ──────▶       NodeState[ ]  (1 per node)
(builders, or raw            (flat pre-order array          byte[ ]       (node data)
 BehaviorNodes.Node)          + end indices)                      │
                                  │                               │ both point into
                                  ▼                               ▼
                             BehaviorTreeLayout ◀────── UnmanagedNodeBlob (ref struct view)
                             one shared native blob:              │
                             topology · type ids · GUID           ▼
                             table · offsets · defaults    VirtualMachine.Tick
```

The layout is the tree's shared, immutable half: end indices (node *i*'s subtree is the
contiguous range `[i, End[i])`, so its first child is `i+1` and a sibling is one jump), a dense
type id per node, a GUID table for durable identity, per-node data offsets packed at each type's
natural alignment, and the authored defaults. A thousand agents share one layout; each owns only
the two buffers. Dispatch goes through a process-wide registry of static-generic invokers — no
reflection, no boxing, no dictionary on the hot path.

**Ownership:** whoever compiles a layout owns it (`IDisposable`, native memory) and must keep it
alive while instances tick against it.

## Writing a node

A node is an unmanaged struct implementing `INodeData`, identified by `[Guid]`. Its **primary
constructor is its exposed surface**: `[Builder]` makes the generator emit a builder class
mirroring those parameters — order and declared defaults included — and everything else in the
struct is runtime state the builder never shows.

```csharp
using Paradise.BT;
using System.Runtime.InteropServices;

[Guid("7D4E31B3-0D57-4211-9C1F-91EAB87734E5")]
[Builder]                                       // emits `Counter`; name defaults to the type minus "Node"
public struct CounterNode(int threshold) : INodeData
{
    public int Threshold = threshold;           // exposed: mirrored by the builder
    private int _count;                         // runtime state: invisible to the builder

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        _count++;                               // persists: a node ticks THROUGH its bytes
        return _count >= Threshold ? NodeState.Success : NodeState.Running;
    }
}
```

- **Field writes persist** across ticks (the node is reinterpreted in place over the instance's
  bytes) and are undone by a reset, which restores the authored value.
- **`record struct`** works as the one-line form: `record struct Patrol(float Radius) : INodeData`.
- **Captured parameters** work too — `struct Cooldown(float seconds)` mutating `seconds` in
  `Tick` is the shortest possible stateful node.
- **Cardinality** is part of `[Builder]`: `[Builder(NodeCardinality.Decorator)]` gives the
  builder a `child` parameter, `Composite` a `params children` span, and the core builder
  validates the counts (a leaf with children or a decorator without exactly one is refused).
- **Optional `Reset` hook**: implement `static virtual void Reset<…>` for side effects beyond
  data restoration (it is static — receiverless — so it cannot read the node's own fields).
- Builder calls passing **two or more values must name them** (`PBT0013`); a single value or a
  child argument stays positional — `Delay(0.5f)` and `Repeat(3, child)` are fine,
  `Forage(0.3f, 6f)` is not.

Registration is automatic: the generator emits one module initializer per assembly. Only a node
it cannot see — private, or declared in a project without the analyzer reference — needs an
explicit `NodeTypeRegistry.Register<T>()`.

## The blackboard

Nodes read and write external data through three methods, and the restraint is deliberate — no
ref returns means a read and a write are statically distinguishable, which is what the whole
generated-contract story stands on:

```csharp
public interface IBlackboard
{
    bool HasData<T>() where T : struct;
    T GetData<T>() where T : struct;
    void SetData<T>(T value) where T : struct;
}
```

Two implementations ship, for two situations:

**The reference `Blackboard`** (`Paradise.BT.Nodes`) is a managed dictionary — right for setup
code, tests, and getting started:

```csharp
BehaviorTree tree = new Selector(
    new Sequence(new Delay(0.5f), new Success()),
    new Failure()
).Build();

var bb = new Blackboard();
BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(bb);

instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.25f));
NodeState state = instance.Tick();   // Running — the delay has 0.25s left
```

Delta time is always the caller's to supply; the runtime never reads a clock, which is what
keeps a run reproducible.

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
            new Delay(0.5f));
}

using BehaviorTreeLayout layout = BehaviorTrees.CompileLayout<PatrolTree>();
BehaviorTreeInstance agent = layout.CreateInstance();

var dt = new BehaviorTreeTickDeltaTime(0.25f);
NodeState state = agent.Tick(PatrolTreeBlackboard.Bind(behaviorTreeTickDeltaTime: in dt));
```

The generator sweeps the tree type for the nodes it composes — through builders, factories
returning builders, `[Builds<T>]`-annotated factories, or `[BehaviorTreeBinding(Also = […])]` as
the last resort — reads each node's `GetData`/`SetData` calls, and emits `PatrolTreeBlackboard`:
a `ref struct` holding `ref readonly` to everything the tree reads and `ref` to everything it
writes, so a write lands in the caller's own storage. **The union of the nodes' access IS the
tree's contract.** Nothing is declared and nothing hand-maintained can drift: remove the last
node reading a type and it leaves the blackboard, and any `Bind` call that no longer matches
fails to compile at the call site.

`PatrolTreeBlackboard` above has exactly one entry — the delay's delta time — including access
from another assembly: a node's declaring assembly publishes its body-scanned access as
`[assembly: NodeAccess]` metadata, so cross-assembly nodes need no `[Reads<T>]`/`[Writes<T>]`
either. The hand-written attributes remain honored for the one case the scan cannot follow — a
node handing its blackboard to a helper method (`PBT0010`).

Pass a generated blackboard **per `Tick` call** (it is a `ref struct`; no field can hold one),
and pass `Bind` arguments **by name** — parameters are ordered by type name, so adding a node
can reorder them.

## Using it inside an ECS

The framework takes no ECS dependency, but its shapes are ECS-shaped on purpose:

- An instance's state is a `NodeState` span plus a byte span — storable inline in a component,
  copied by any world-snapshot memcpy, pointed at a shared layout through the unmanaged
  `BehaviorTreeLayoutHandle`.
- The raw path skips the instance class entirely: build an `UnmanagedNodeBlob` over your own
  memory each tick and call the VM.

```csharp
var blob = new UnmanagedNodeBlob(layoutHandle, statesSpan, dataSpan);
NodeState state = VirtualMachine.Tick(blob, blackboard);
```

- A type marked `[Component]` (or implementing an interface named `Paradise.ECS.IComponent` —
  recognized structurally, no reference taken) binds **read-only** in a generated blackboard,
  and a node that writes one is refused at compile time (`PBT0008`). The pattern that follows:
  a tree writes a *conclusion* — plain structs like an `Intent` — and the owning system applies
  it to the world. That is what lets one tree drive bodies steered any way you like.

## Serialization

The layout blob is the shippable asset — a raw, position-independent byte block:

```csharp
byte[] bytes = layout.SerializeToBytes();              // or tree.SerializeLayoutToBytes()
using BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(bytes);
```

- Node identity crosses as a **GUID table**, one entry per distinct type; process-local ids are
  re-resolved against the registry on load.
- Loading **validates before anything ticks**: format version, array bounds, topology sanity,
  and that every GUID is registered with a compatible size.
- Nothing process-local reaches the bytes, so the same tree serializes **identically in every
  process** — layout blobs are content-hashable and diffable.

A second form, `tree.Serialize()` / `BehaviorTreeBlobSerializer.Deserialize(bytes)`, round-trips
a managed `BehaviorTree` for interchange and re-editing.

## Diagnostics

All enforced at compile time by `Paradise.BT.Generators`:

| id | says |
|---|---|
| `PBT0001` | a `[Builder]` node is missing `[Guid]` |
| `PBT0002` | a `[Builder]` node contains managed references (warning) |
| `PBT0003` | two node types share a GUID |
| `PBT0006` | `[OptionalReads<T>]` is not supported |
| `PBT0008` | a node writes a component — components bind read-only by value; write a conclusion |
| `PBT0009` | a node's body touches something its declared access omits |
| `PBT0010` | a blackboard handed to a method the access scan cannot follow (warning) |
| `PBT0011` | a node declares more than one public constructor — the surface is ambiguous |
| `PBT0012` | a public field is not part of the node's constructor surface (warning) |
| `PBT0013` | a builder call passes several values positionally — name them |

## Design notes

- Instances are unmanaged so trees can live *inside* a simulation — in components, in snapshots
  — rather than beside it in a managed side table where timers escape every snapshot and
  decisions arrive a frame late.
- The tick path allocates nothing: dispatch is a dense-id lookup into static-generic invokers,
  and the generated blackboard's `GetData<T>`/`SetData<T>` fold to direct field access at JIT
  time.
- Determinism is a design goal throughout: no clocks, no reflection at tick time, and
  byte-stable serialization.

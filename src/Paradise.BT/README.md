# Paradise.BT

`Paradise.BT` is a pure .NET behavior tree runtime with unmanaged struct nodes, inspired by
`EntitiesBT`. A compiled tree is flattened into one shared `Paradise.BLOB` asset — the layout —
and an instance owns only a `NodeState` per node plus its node data as bytes, so a thousand
agents share one layout and an instance fits in an ECS component and survives a memcpy.

## Install

```bash
dotnet add package Paradise.BT
dotnet add package Paradise.BT.Builder   # authoring: BTreeNode, IBehaviorTreeBuilder, BehaviorTrees
dotnet add package Paradise.BT.Nodes     # built-ins + the reference Blackboard
```

Projects that **declare node types or trees** must also reference `Paradise.BT.Generators` as an
analyzer: it registers every node type via a module initializer, emits the builder classes, the
per-tree blackboards, and the access metadata, and enforces the PBT diagnostics.

## Features

- Pure .NET (`net10.0`), NativeAOT/trimming compatible, zero allocation on the tick path.
- Built-ins: sequence, selector, parallel, repeat, repeat-forever, inverter, succeeder, delay,
  success, failure, running.
- Nodes are unmanaged structs; a node's **primary constructor is its exposed surface**, mirrored
  into a generated builder (`record struct` works too).
- A **generated, per-tree blackboard**: the union of what the tree's nodes actually touch, read
  from their `Tick` bodies — nothing declared, nothing to drift stale.
- The layout blob is **directly serializable and deterministic**: the same tree produces the same
  bytes in every process, so assets are content-hashable.

## Quick start

```csharp
using Paradise.BT;
using Paradise.BT.Nodes;
using Paradise.BT.Nodes.Builder;

BehaviorTree tree = new Selector(
    new Sequence(
        new Delay(0.5f),
        new Success()),
    new Failure()
).Build();

var bb = new Blackboard();
BehaviorTreeInstance<Blackboard> instance = tree.CreateInstance(bb);

instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.25f));
NodeState first = instance.Tick();    // Running: the delay has 0.25s left
instance.Blackboard.SetData(new BehaviorTreeTickDeltaTime(0.25f));
NodeState second = instance.Tick();   // Success
```

Delta time is the caller's to supply — the runtime never reads a clock.

## Custom nodes

An unmanaged struct implementing `INodeData`, identified by `[Guid]`. `[Builder]` makes the
generator emit a builder class whose constructor mirrors the node's primary constructor —
parameters, order, and declared defaults. Anything not in the constructor (a private field, a
captured parameter) is runtime state the builder never shows.

```csharp
using Paradise.BT;
using System.Runtime.InteropServices;

[Guid("7D4E31B3-0D57-4211-9C1F-91EAB87734E5")]
[Builder]
public struct CounterNode(int threshold) : INodeData
{
    public int Threshold = threshold;
    private int _count;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        _count++;
        return _count >= Threshold ? NodeState.Success : NodeState.Running;
    }
}

// Composes as: new Counter(3)
```

A node writing its own fields persists them — it ticks through its bytes in the instance, and a
reset restores the authored defaults. A builder call passing **two or more** values must name
them (`PBT0013`); one value stays positional.

## Trees, and the generated blackboard

A tree is a type implementing `IBehaviorTreeBuilder` (or `IBehaviorTreeBuilder<TArgs>` when it is
built from a config). That is the whole marker: the generator sweeps the type for the nodes it
composes, unions their access, and emits `{Tree}Blackboard` with a `Bind` — the union IS the
tree's contract.

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

`PatrolTreeBlackboard` here has exactly one entry — the delay's delta time — because that is all
this tree touches, and nobody declared it anywhere: the generator read `DelayTimerNode`'s body in
its own assembly and published the access as metadata. The generated blackboard is a `ref struct`
of refs into the caller's storage (ECS chunk memory, locals), so pass it per `Tick` call; ECS
components bind read-only, and a node that tries to write one is refused at compile time
(`PBT0008`) — a tree writes conclusions the caller applies.

## Serialization

The layout blob is the shippable asset — a raw, position-independent byte block:

```csharp
byte[] bytes = layout.SerializeToBytes();              // or tree.SerializeLayoutToBytes()
using BehaviorTreeLayout loaded = BehaviorTreeLayout.Deserialize(bytes);
```

Node identity crosses as a GUID table (one entry per distinct type); process-local ids are
re-resolved on load, corrupt bytes are refused at load, and serialization is deterministic. The
`BehaviorTreeBlob` form (`tree.Serialize()` / `BehaviorTreeBlobSerializer.Deserialize`) remains
as managed interchange. Node types resolve through `NodeTypeRegistry`, populated by the
generator's module initializers — only a node the generator cannot see (private, or no analyzer
reference) needs an explicit `NodeTypeRegistry.Register<T>()`.

## Notes

- `BehaviorTreeInstance` is two plain buffers over a borrowed layout; the layout must outlive its
  instances, and whoever compiles it disposes it.
- `NodeState.None` (0) means "never ticked / reset since" — a real state decorators branch on.
- The reference `Blackboard` in `Paradise.BT.Nodes` is a managed dictionary — right for setup and
  tests; the generated per-tree blackboard is the zero-allocation path.

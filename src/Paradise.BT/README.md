# Paradise.BT

`Paradise.BT` is a pure .NET behavior tree runtime with struct-based nodes, an `EntitiesBT`-style blackboard API, and optional binary serialization through `Paradise.BLOB`.

## Install

```bash
dotnet add package Paradise.BT
```

## Features

- Pure .NET runtime with no Unity or ECS dependency.
- Targets `netstandard2.1` and `net10.0`.
- Built-in sequence, selector, parallel, repeat, repeat-forever, inverter, succeeder, delay, success, failure, running, delegate action, and delegate condition nodes.
- Immutable compiled trees plus reusable `BehaviorTreeInstance` runtime state.
- Custom node authoring via unmanaged `struct` types that implement `INodeData`.
- Serialization and deserialization support through `Paradise.BLOB`.

## Quick start

```csharp
using Paradise.BT;

public struct HasTargetData
{
    public bool Value;
}

public struct ShotsFiredData
{
    public int Value;
}

var blackboard = new Blackboard();
blackboard.SetData(new HasTargetData { Value = true });
blackboard.SetData(new ShotsFiredData());

var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Selector(
        BehaviorNodes.Sequence(
            BehaviorNodes.Condition(static bb => bb.GetData<HasTargetData>().Value),
            BehaviorNodes.Repeat(
                3,
                BehaviorNodes.Sequence(
                    BehaviorNodes.Delay(0.5f),
                    BehaviorNodes.Action(static bb =>
                    {
                        ref ShotsFiredData shots = ref bb.GetDataRef<ShotsFiredData>();
                        shots.Value++;
                        return NodeState.Success;
                    })))),
        BehaviorNodes.Action(static _ => NodeState.Success)));

BehaviorTreeInstance instance = tree.CreateInstance(blackboard);

NodeState firstTick = instance.Tick(0.25f);
NodeState secondTick = instance.Tick(0.25f);
```

## Custom nodes

Implement `INodeData` on a `struct` and decorate it with `GuidAttribute` so it can participate in serialization.

```csharp
using Paradise.BT;
using System.Runtime.InteropServices;

[Guid("7D4E31B3-0D57-4211-9C1F-91EAB87734E5")]
public struct CounterNode : INodeData
{
    public int Count;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        Count++;
        return Count >= 3 ? NodeState.Success : NodeState.Running;
    }
}

var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Node(new CounterNode()));
```

If a node needs reset side effects in addition to restoring its default struct data, also implement `ICustomResetAction`.

## Serialization

Compiled trees can be turned into binary blobs and loaded back later.

```csharp
using Paradise.BT;

using var blob = tree.Serialize();
BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob);
```

Custom unmanaged nodes must be registered before deserialization.

```csharp
var registry = new BehaviorTreeSerializationRegistry()
    .Register<CounterNode>();

BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob, registry);
```

## Notes

- `BehaviorTreeInstance.Tick(deltaTime)` stores the supplied delta time in the blackboard, which is how nodes such as `BehaviorNodes.Delay(...)` advance.
- Delegate-backed nodes are runtime-only and cannot be serialized because they capture managed references.
- The `Blackboard` type also supports object and keyed-value storage helpers for app-level data setup.

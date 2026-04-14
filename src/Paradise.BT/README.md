# Paradise.BT

Paradise.BT is a pure .NET behavior tree runtime with a project layout modeled after `Paradise.ECS` and a node-facing API intentionally aligned with `EntitiesBT`.

The public runtime contracts follow the same shape as `EntitiesBT`:

- `INodeData`
- `ICustomResetAction`
- `INodeBlob`
- `IBlackboard`
- `GuidAttribute`
- `BehaviorNodeType`
- `NodeState`
- `VirtualMachine`
- `NodeExtensions`

Node authoring uses `struct` nodes with interface constraints, not a class inheritance hierarchy.

## Features

- Pure .NET library with no Unity or ECS dependency.
- Targets `netstandard2.1` for reusable runtime consumption.
- Mirrors the `EntitiesBT` node contract style with `struct` node data and generic `Tick<TNodeBlob, TBlackboard>` methods.
- Compiles authored trees into a flat preorder layout with subtree end indices.
- Serializes compiled trees through `Paradise.BLOB`.
- Keeps authored node defaults separate from per-instance runtime node state.
- Includes built-in sequence, selector, parallel, repeat, repeat-forever, inverter, succeeder, delay, success, failure, running, delegate action, and delegate condition nodes.
- Ships with a console sample and TUnit-based NativeAOT test coverage.

## Project Layout

- `src/Paradise.BT` - core runtime library
- `src/Paradise.BT.Sample` - console sample app
- `src/Paradise.BT.Test` - TUnit test project
- `src/Directory.Build.props` - shared SDK/analyzer settings
- `src/Directory.Packages.props` - central package versions

## Quick Start

```csharp
using Paradise.BT;

public struct ShotsFiredData
{
    public int Value;
}

var blackboard = new Blackboard();
blackboard.SetData(new ShotsFiredData());

var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Sequence(
        BehaviorNodes.Delay(0.5f),
        BehaviorNodes.Action(static bb =>
        {
            ref ShotsFiredData shots = ref bb.GetDataRef<ShotsFiredData>();
            shots.Value++;
            Console.WriteLine($"Shot #{shots.Value}");
            return NodeState.Success;
        })));

BehaviorTreeInstance instance = tree.CreateInstance(blackboard);

Console.WriteLine(instance.Tick(0.25f));
Console.WriteLine(instance.Tick(0.25f));
```

## Blackboard Usage

Inside nodes, rely on the exact `EntitiesBT`-style `IBlackboard` contract:

```csharp
public struct CooldownData
{
    public float Value;
}

ref CooldownData cooldown = ref bb.GetDataRef<CooldownData>();
cooldown.Value -= bb.GetData<BehaviorTreeTickDeltaTime>().Value;
```

The concrete `Blackboard` type also provides convenience setup helpers for application code:

```csharp
var blackboard = new Blackboard();
blackboard.SetData(new CooldownData { Value = 1.5f });
blackboard.SetObject(new StringBuilder("ready"));
blackboard.Set("target-name", "Drone");
```

## Custom Nodes

Create custom nodes by implementing `INodeData` on a `struct` and decorating it with `GuidAttribute`.

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
```

If a node needs reset side effects in addition to restoring its default struct data, implement `ICustomResetAction`:

```csharp
using Paradise.BT;
using System.Runtime.InteropServices;

[Guid("E0386AF3-83B6-40EC-918A-2A0B8184C6E6")]
public struct ResetAwareNode : INodeData, ICustomResetAction
{
    public int Count;

    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        Count++;
        return Count >= 2 ? NodeState.Success : NodeState.Running;
    }

    public void Reset<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        Console.WriteLine($"Reset node {index}");
    }
}
```

Author custom nodes through the constrained helper:

```csharp
var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Node(new CounterNode()));
```

For custom decorators or composites, use the overload that specifies `BehaviorNodeType` explicitly:

```csharp
var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Node(new CounterNode(), BehaviorNodeType.Composite, childA, childB));
```

## BLOB Serialization

Compiled trees can be serialized through `Paradise.BLOB` and restored later:

```csharp
using var blob = tree.Serialize();
BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob);
```

Custom unmanaged nodes must be registered before deserialization:

```csharp
var registry = new BehaviorTreeSerializationRegistry()
    .Register<CounterNode>();

BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob, registry);
```

Delegate-backed nodes stay runtime-only and are intentionally rejected because they contain managed references.

## Build And Test

```bash
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test --project src/Paradise.BT.Test/Paradise.BT.Test.csproj -p:PublishAot=false --output normal
```

## NativeAOT

Paradise.BT is set up for NativeAOT-friendly consumption:

- `src/Paradise.BT` multi-targets `netstandard2.1` and `net10.0`.
- `src/Paradise.BT.Sample` enables `PublishAot=true` for an executable smoke test.
- `src/Paradise.BT.Test` enables `PublishAot=true` so TUnit also runs as a native binary.

Example sample publish:

```bash
dotnet publish src/Paradise.BT.Sample/Paradise.BT.Sample.csproj -c Release -r osx-arm64
./src/Paradise.BT.Sample/bin/Release/net10.0/osx-arm64/publish/Paradise.BT.Sample
```

Example NativeAOT test publish and execution:

```bash
dotnet publish src/Paradise.BT.Test/Paradise.BT.Test.csproj -c Release -r osx-arm64
./src/Paradise.BT.Test/bin/Release/net10.0/osx-arm64/publish/Paradise.BT.Test
```

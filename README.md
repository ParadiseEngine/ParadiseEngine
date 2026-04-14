# Paradise Engine

Paradise Engine is initialized as a .NET monorepo with all projects living directly under a shared `src/` tree.

## Monorepo layout

- `src/Paradise.BT` - the behavior tree runtime library.
- `src/Paradise.BT.Sample` - console sample app for the behavior tree runtime.
- `src/Paradise.BT.Test` - TUnit coverage for the behavior tree runtime.
- `src/Paradise.BLOB` - the standalone BLOB builder library used by BT serialization.
- `src/Paradise.BLOB.Test` - TUnit coverage for the BLOB builder library.
- `ParadiseEngine.slnx` - top-level solution covering all projects.
- `src/Directory.Build.props` and `src/Directory.Packages.props` - shared SDK and package settings for all projects.

## Build and test

```bash
dotnet build --solution ParadiseEngine.slnx
dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal
```

Package-specific notes live in:

- `src/Paradise.BT/README.md`
- `src/Paradise.BLOB/README.md`

## Behavior tree serialization

`Paradise.BT` now serializes compiled trees through `Paradise.BLOB`:

```csharp
using Paradise.BT;

var tree = BehaviorTreeBuilder.Build(
    BehaviorNodes.Sequence(
        BehaviorNodes.Delay(0.5f),
        BehaviorNodes.Success()));

using var blob = tree.Serialize();
BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob);
```

For custom nodes, register the unmanaged node types that can be deserialized:

```csharp
var registry = new BehaviorTreeSerializationRegistry()
    .Register<MyCustomNode>();

BehaviorTree roundTripped = BehaviorTreeBlobSerializer.Deserialize(blob, registry);
```

Delegate-backed nodes remain runtime-only and are intentionally rejected by the serializer because they contain managed references.

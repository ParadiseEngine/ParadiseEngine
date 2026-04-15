# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
# Build all projects
dotnet build --solution ParadiseEngine.slnx

# Run all tests
dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal

# Build/test a single project
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj -p:PublishAot=false --output normal

# Run the sample app
dotnet run --project src/Paradise.BT.Sample/Paradise.BT.Sample.csproj
```

The `-p:PublishAot=false` flag is required for tests because test projects enable AOT but TUnit needs it disabled at test time.

## Project Overview

Paradise Engine is a .NET behavior tree runtime library inspired by EntitiesBT, with a companion binary blob serialization library. It targets `netstandard2.1` + `net10.0` (dual-target), uses C# 14, and is NativeAOT/trimming compatible.

### Monorepo Layout

- `src/Paradise.BLOB` — Standalone unmanaged binary blob builder (BlobArray, BlobString, BlobPtr, builders). No external dependencies. Target: `netstandard2.1`.
- `src/Paradise.BT` — Behavior tree runtime built on top of Paradise.BLOB. Target: `netstandard2.1;net10.0`.
- `src/Paradise.BT.Sample` — Console sample demonstrating tree construction, blackboard usage, and ticking.
- `src/Paradise.BT.Test` / `src/Paradise.BLOB.Test` — TUnit test suites.
- `src/Directory.Build.props` — Shared build properties (C# 14, nullable, unsafe, warnings-as-errors).
- `src/Directory.Packages.props` — Centralized NuGet package versions.
- `ParadiseEngine.slnx` — Solution file (modern slnx format).

## Architecture

### Behavior Tree Pipeline

1. **Authoring** — `BehaviorNodeDefinition` is the mutable tree representation. Use `BehaviorNodes` static factory methods (`Sequence`, `Selector`, `Delay`, `Action`, etc.) to compose trees declaratively.
2. **Compilation** — `BehaviorTreeBuilder.Build(definition)` validates the tree (child count per node type) and produces an immutable `BehaviorTree` with a flat node array + end indices.
3. **Instantiation** — `tree.CreateInstance()` returns a `BehaviorTreeInstance` with its own `NodeBlob` (runtime state) and `Blackboard`.
4. **Execution** — `instance.Tick(deltaTime)` drives one frame via `VirtualMachine.Tick()`, which dispatches to each node's `IRuntimeNode` implementation.
5. **Serialization** (optional) — `tree.Serialize()` compiles to Paradise.BLOB binary format. `BehaviorTreeBlobSerializer.Deserialize(blob, registry)` round-trips. Delegate-backed nodes cannot be serialized (managed references).

### Key Abstractions

- **`INodeData`** — The core node contract. All custom nodes must be `struct` types implementing this. Generic `Tick<TNodeBlob, TBlackboard>` method receives index, blob, and blackboard by ref.
- **`ICustomResetAction`** — Optional interface for nodes with stateful reset behavior.
- **`INodeBlob` / `NodeBlob`** — Stores per-instance runtime state: `IRuntimeNode[]`, end indices, and `NodeState[]` per node.
- **`IBlackboard` / `Blackboard`** — Mutable data store for shared state. Supports typed struct/object storage and named key-value pairs.
- **`BehaviorNodeType`** — Enum (`Action`, `Decorate`, `Composite`) that governs child-count validation.
- **`NodeState`** — Flags enum (`Success`, `Failure`, `Running`).

### Custom Node Pattern

Implement `INodeData` on an unmanaged struct, tag with `[Guid("...")]` for serialization, then use `BehaviorNodes.Node(new MyNode(), children)` to include in a tree:

```csharp
[Guid("...")]
public struct MyNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob
        where TBlackboard : struct, IBlackboard
    {
        // access runtime/default data via blob.GetNodeData<MyNode>(index)
        // access shared state via bb.GetData<T>()
        return NodeState.Success;
    }
}
```

### Paradise.BLOB

Low-level unmanaged binary blob library used by BT serialization. Key types: `BlobArray<T>`, `BlobString<TEncoding>`, `BlobPtr<T>`, `ManagedBlobAssetReference<T>`. Builders (`ValueBuilder`, `StructBuilder`, `ArrayBuilder`, `TreeBuilder`, `SortedArrayBuilder`) produce pinned memory blocks.

## Code Style

Enforced via `.editorconfig` with warnings-as-errors:
- **Naming**: private/internal fields `_camelCase`, statics `s_camelCase`, constants `PascalCase`, public fields/properties `PascalCase`
- **Layout**: Allman braces, 4-space indent, file-scoped namespaces, LF line endings
- **Types**: Prefer language keywords (`int` not `Int32`), avoid `this.` qualification
- **Performance**: Struct-based nodes, `ref` parameters throughout, zero-allocation design, `System.Runtime.CompilerServices.Unsafe` for low-level ops

## SDK

Requires .NET SDK 10.0.200+ (specified in `global.json` with `rollForward: latestMinor`).

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
# Build all projects
dotnet build --solution ParadiseEngine.slnx

# Run all tests
dotnet test --solution ParadiseEngine.slnx --output normal

# Build/test a single project
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal

# Run the sample app
dotnet run --project src/Paradise.BT.Sample/Paradise.BT.Sample.csproj
```

AOT compatibility of tree construction and ticking is verified via `Paradise.BT.Sample`, which sets `<PublishAot>true</PublishAot>`. Test projects do not enable AOT so the analyzer-testing harness can use `Reflection.Emit`. The `Paradise.BT` serialization surface (`Serialize`/`Deserialize`) and `Paradise.BLOB`'s `ManagedBlobAssetReference` are not currently covered by an AOT build; adding a dedicated AOT publish-and-run CI job for those paths is a known follow-up.

### Concurrent code gets a Coyote test

**Anything with cross-thread rules — a lock, a shared flag, a queue two threads touch — gets a
systematic test in the matching `*.CoyoteTest` project, not only a stress loop.** Coyote schedules
interleavings deliberately; a stress loop reaches the bad one by luck or not at all. This is not
theoretical: a hand-written race test for the renderer's capture queue passed **three runs out of
three** against code with a real check-then-enqueue defect, while the Coyote test on the same
broken build failed inside 200 iterations.

```bash
# Release, because the `coyote rewrite` target only runs there (needs the coyote CLI)
dotnet build src/Paradise.Rendering.WebGPU.CoyoteTest/... -c Release
dotnet run --project src/Paradise.Rendering.WebGPU.CoyoteTest -c Release -- 200
```

Existing suites: `Paradise.ECS.CoyoteTest`, `Paradise.Rendering.WebGPU.CoyoteTest`.

Three things worth knowing before writing one:

- **Extract the managed part first.** Coyote schedules `Task`, `lock` and concurrent collections —
  it cannot see inside a native call. The renderer's capture path is mostly Dawn
  (`OnSubmittedWorkSync`, `MapSync`, `RequestAdapterSync`), so the queue, its flag and its drain
  were pulled into `CaptureQueue`, which has no native calls at all. Testability was the reason,
  and it is usually the reason such an extraction is worth it.
- **Await joins; do not block on them.** `Task.WaitAll` parks a thread, which Coyote cannot
  distinguish from a deadlock — it reports every such test as a potential hang even when the code
  is correct. Making the tests `async` keeps hang detection ON and meaningful, instead of switching
  it off with `WithPotentialDeadlocksReportedAsBugs(false)`.
- **Prove the test fails without the fix.** Reintroduce the defect, watch it fail, restore. A
  concurrency test that has never failed is a guard nobody has checked the lock on.

These projects are deliberately NOT `IsTestProject` — they are standalone runners with their own
`Main`, so `dotnet test` skips them and they must be run explicitly.

## Project Overview

Paradise Engine is a .NET behavior tree runtime library inspired by EntitiesBT, with a companion binary blob serialization library. It targets `net10.0`, uses C# 14, and is NativeAOT/trimming compatible.

### Coordinate convention

The engine and its data contract are **right-handed: Y-up, −Z forward, +X right** (Godot / glTF
standard), in meters, with **column-major** matrices. This matches what the editor tools
(`ParadiseGodotEditor`) export — the exporter writes Godot values verbatim, with no handedness
conversion. Any future scene/navmesh/level loader must consume right-handed data directly (no
Z-mirror). The engine core (`Paradise.ECS`, `Paradise.Rendering`) is otherwise coordinate-agnostic;
handedness only enters where transforms, camera/projection matrices, or navmesh geometry are built.

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

1. **Authoring** — generated builder classes (from `[Builder]` via `BTreeNodeGenerator`) or raw `BehaviorNodes.Node(...)` compose a mutable `BehaviorNodeDefinition` tree.
2. **Compilation** — `BehaviorTreeBuilder.Build(definition)` validates child counts against each node's `[Builder]` cardinality (Leaf = 0, Decorator = 1; nodes without the attribute are not checked) and produces an immutable `BehaviorTree` (flat pre-order array + end indices).
3. **Layout** — `BehaviorTreeLayout.Build(tree)` flattens into a shared, immutable `NodeBlob` in native memory: end indices, node type ids, GUIDs, per-node data offsets (packed at each node's natural alignment), and authored defaults. A thousand agents share one layout.
4. **Instantiation** — `BehaviorTreeInstance` owns the per-agent state (a `NodeState[]` and a `byte[]` of node data) and takes the blackboard per `Tick(bb)` call, so `ref struct` (generated) blackboards work. `BehaviorTreeInstance<TBlackboard>` adds an owned blackboard for plain-struct blackboards; `tree.CreateInstance(...)` / `layout.CreateInstance()` construct them.
5. **Execution** — `VirtualMachine.Tick()` looks each node up in `NodeTypeRegistry` by dense id and ticks it through its bytes. Registration is emitted per assembly by the generator as a module initializer.
6. **Serialization** — two forms. **Layout bytes** (preferred for shipping): `layout.SerializeToBytes()` / `tree.SerializeLayoutToBytes()` is a raw copy of the position-independent blob; `BehaviorTreeLayout.Deserialize(bytes)` validates it and re-resolves GUIDs to this process's type ids — no managed tree, no registry argument. **Interchange**: `tree.Serialize()` via `BehaviorTreeBlobSerializer` round-trips a managed `BehaviorTree` through a `BehaviorTreeSerializationRegistry`.

### Key Abstractions

- **`INodeData`** — The core node contract: unmanaged struct with generic `Tick<TNodeBlob, TBlackboard>(int index, blob, bb)`; optional `static virtual Reset`. Identity is `[Guid]`.
- **`INodeBlob` / `UnmanagedNodeBlob`** — Blob contract over a shared `NodeBlob*` plus caller-owned spans (states + runtime data). Data is reached by `ref byte`, so buffers may be managed arrays or native/chunk memory.
- **`NodeTypeRegistry`** — Process-wide GUID → dense id → invoker table; ids are process-local, GUIDs are the durable identity.
- **`IBlackboard`** — Three members (`HasData`/`GetData`/`SetData`), no ref returns, which is what makes read/write intent statically checkable by the generators.
- **`NodeState`** — Flags enum (`None`, `Success`, `Failure`, `Running`); `None` means "never ticked / reset".

### Custom Node Pattern

Implement `INodeData` on an unmanaged struct, tag with `[Guid("...")]` for serialization, then use `BehaviorNodes.Node(new MyNode(), children)` to include in a tree:

```csharp
[Guid("...")]
public struct MyNode : INodeData
{
    public NodeState Tick<TNodeBlob, TBlackboard>(int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
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

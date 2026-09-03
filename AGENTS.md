# ParadiseEngine — agent guide

A .NET game engine: an ECS, a WebGPU renderer, an unmanaged behavior-tree runtime, an asset
pipeline and the packages a game consumes them through. This file is the canonical guidance for
AI agents; `CLAUDE.md` imports it.

## Build and Test Commands

```bash
# Build all projects
dotnet build ParadiseEngine.slnx

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

Existing suites: `Paradise.ECS.CoyoteTest`, `Paradise.Rendering.WebGPU.CoyoteTest`,
`Paradise.Assets.Pipeline.CoyoteTest`, `Paradise.Assets.Project.CoyoteTest`, `Paradise.Cli.CoyoteTest`,
`Paradise.Ui.ImGui.CoyoteTest`.

A fourth thing, learned from the asset watcher: **lock on an `object`, not on
`System.Threading.Lock`, in anything a Coyote suite covers.** Coyote (1.7.11) rewrites
`Monitor.Enter`/`Exit` and does not intercept `Lock.EnterScope`, so with the newer type it cannot
control the lock — every iteration reports the wait as a potential hang, and silencing that would
only hide the fact that the interleavings around that lock are never explored. The newer type is
worth having where a lock is hot; it is not worth a suite that cannot see it.

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

- `src/Paradise.BLOB` — Standalone unmanaged binary blob builder (BlobArray, BlobString, BlobPtr, builders). No external dependencies. Target: `net10.0`.
- `src/Paradise.BT` — Behavior tree runtime built on top of Paradise.BLOB. Target: `net10.0`.
- `src/Paradise.BT.Sample` — Console sample demonstrating tree construction, blackboard usage, and ticking.
- `src/Paradise.BT.Test` / `src/Paradise.BLOB.Test` — TUnit test suites.
- `src/Directory.Build.props` — Shared build properties (C# 14, nullable, unsafe, warnings-as-errors).
- `src/Directory.Packages.props` — Centralized NuGet package versions.
- `ParadiseEngine.slnx` — Solution file (modern slnx format).

## Architecture

### Behavior Tree Pipeline

1. **Authoring** — generated builder classes (from `[Builder]` via `BTreeNodeGenerator`) or the raw generic wrappers (`LeafNode<T>` / `DecoratorNode<T>` / `CompositeNode<T>`) compose a `BTreeNode` graph (`Paradise.BT.Builder`).
2. **Compilation** — `BTreeNode.Build()` validates each builder's arity against its node's `[Builder]` cardinality (Leaf = 0, Decorator = 1; no attribute claims Leaf) and flattens straight into a `BehaviorTreeLayout`: one shared native blob of end indices, a GUID table, per-node data offsets (natural alignment, capped at 16) and authored defaults. `BehaviorTrees.Compile<TTree>()` does the same from a tree TYPE and returns a typed `BehaviorTreeLayout<TTree>`. A thousand agents share one layout; there is no serialization — trees compile from code.
3. **Instantiation** — an instance is two caller-owned buffers over the layout: `BehaviorTreeRef` (a ref struct view) for arbitrary spans, or `FixedBehaviorTree<TTree, TStates, TData>` for inline-in-a-component storage. The blackboard is passed per `Tick(bb)` call, so `ref struct` (generated) blackboards work.
4. **Execution** — `VirtualMachine.Tick()` dispatches each node by its GUID through `NodeTypeRegistry` and ticks it through its bytes. Registration is emitted per assembly by the generator as a module initializer.
5. **Type safety** — the binding generator stamps each generated blackboard `IBlackboardFor<TTree>`; the typed layout/ref and `FixedBehaviorTree` only accept that tree's blackboard, so a mismatch is a compile error.

### Key Abstractions

- **`INode`** — The core node contract: unmanaged struct with generic `Tick<TBehaviorTree, TBlackboard>(int index, blob, bb)`; optional `static virtual Reset`. Identity is `[Guid]`.
- **`IBehaviorTree` / `BehaviorTreeRef`** — The instance view over the shared `LayoutBlob` plus caller-owned spans (states + runtime data). Data is reached by `ref byte`, so buffers may be managed arrays or native/chunk memory.
- **`NodeTypeRegistry`** — Process-wide GUID → invoker table; the GUID is the whole identity.
- **`IBlackboard`** — Three members (`HasData`/`GetData`/`SetData`), no ref returns, which is what makes read/write intent statically checkable by the generators.
- **`NodeState`** — Flags enum (`None`, `Success`, `Failure`, `Running`); `None` means "never ticked / reset".

### Custom Node Pattern

Implement `INode` on an unmanaged struct, tag with `[Guid("...")]` (and `[Builder]` for a generated builder class), then compose it via its builder or `new LeafNode<MyNode>(...)`:

```csharp
[Guid("...")]
public struct MyNode : INode
{
    public NodeState Tick<TBehaviorTree, TBlackboard>(int index, TBehaviorTree blob, TBlackboard bb)
        where TBehaviorTree : struct, IBehaviorTree, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
    {
        // access runtime/default data via blob.GetNodeData<MyNode>(index)
        // access shared state via bb.GetData<T>()
        return NodeState.Success;
    }
}
```

### Paradise.BLOB

Low-level unmanaged binary blob library backing the BT layout. Key types: `BlobArray<T>`, `BlobString<TEncoding>`, `BlobPtr<T>`, `ManagedBlobAssetReference<T>`. Builders (`ValueBuilder`, `StructBuilder`, `ArrayBuilder`, `TreeBuilder`, `SortedArrayBuilder`) produce pinned memory blocks.

### Diagnostics go through `ILogger`, never `Console`

**An engine library takes an `ILogger` and references `Microsoft.Extensions.Logging.Abstractions`
and nothing else.** Which sink a game logs to is the host's decision, the same way the mount is
(above). `Paradise.Diagnostics` is one sink, used by `Paradise.Cli.Host`; adding a *provider* —
ZLogger, Serilog, `Microsoft.Extensions.Logging.Console` — to a `Paradise.*` library decides for
every host at once and is the mistake the rule exists to prevent.

Three things that are not obvious, all of which the build will teach you the hard way:

- **Use `[LoggerMessage]`, not `logger.LogInformation(...)`.** The generator ships inside the
  Abstractions package and emits the `IsEnabled` check BEFORE touching arguments, so a disabled
  level costs no boxing and no template parse. It needs a `partial` class, and its `ILogger`
  parameter **cannot be nullable** — the generated body calls `IsEnabled` unguarded, so `ILogger?`
  fails with CS8602 inside generated code. Carry `NullLogger.Instance` instead.
- **Log a `UPath` as an argument, never a pre-rendered string.** The reader does not know what its
  filesystem is mounted over and must not guess; the host does, and installs a renderer
  (`ParadiseConsoleOptions.RenderValue`). A `Display`-style helper that shortens a path inside a
  library is one host's preference in the layer that cannot know it.
- **Thread safety is the sink's job.** Dawn, Noesis and SDL all call back on threads the engine did
  not create, and `ILogger` promises no affinity.

Program output is not a diagnostic: `Verbs` printing `verify: 3 error(s)` and
`Paradise.Authoring.SchemaDump` writing its dump keep `Console.WriteLine`. See `docs/logging.md`.

### Everything that reads content takes an `IFileSystem`, not a path

**A reader takes a Zio `IFileSystem` and a `UPath`; the HOST decides what that is mounted over.**
This is one vocabulary across both halves of the engine — the asset pipeline was already on Zio
(`Paradise.Assets.Pipeline`, `Paradise.Assets.Documents`), and the runtime readers
(`AuthoredDocument.Load`, `Paradise.Ui.Noesis`'s XAML/texture/font providers) now are too. A
shipped build can mount an archive, the editor mounts its play tree, a test mounts memory.

Three things follow, and each replaced code somebody had written by hand:

- **Do not translate separators.** A `UPath` is `/`-separated on every platform, which is exactly
  how the asset contract spells a field, so a field combines onto its root verbatim. Every
  `Replace('/', Path.DirectorySeparatorChar)` in a reader is a sign the mount was not used.
- **Containment is the mount's, not a check you write.** Combining a `..` that climbs past the
  root throws, and an absolute uri resolves INSIDE the mount rather than escaping it — so
  untrusted content (a GLB's image uris) is confined by a `SubFileSystem` over the file's own
  directory. Wrap the refusal only to name what asked for it; do not re-implement the rule.
- **A test mounts memory rather than a temp directory.** `MemoryFileSystem` needs no cleanup, so
  no fixture survives a test that throws before its `finally`. `SubFileSystem` over the test's
  output directory is what makes fixture paths read like a shipped tree.

**The exception, and why it is one:** `Paradise.Audio.Wwise` keeps host paths. The native loader
opens the bank file itself, so no mount can back it, and an abstraction that lies at that layer is
worse than none. When a reader genuinely cannot go through the mount, say so where it does not.

Two things a wrapper must respect, both learned the hard way (see `.claude/lessons.md`):
`CopyFileCross` resolves through a composed filesystem down to the physical one and never reaches
a subclass's `OpenFileImpl`, and a Coyote spec over `MemoryFileSystem` can pass against a missing
lock because memory tolerates what the OS refuses.

## Code Style

Enforced via `.editorconfig` with warnings-as-errors:
- **Naming**: private/internal fields `_camelCase`, statics `s_camelCase`, constants `PascalCase`, public fields/properties `PascalCase`
- **Layout**: Allman braces, 4-space indent, file-scoped namespaces, LF line endings
- **Types**: Prefer language keywords (`int` not `Int32`), avoid `this.` qualification
- **Performance**: Struct-based nodes, `ref` parameters throughout, zero-allocation design, `System.Runtime.CompilerServices.Unsafe` for low-level ops
- **Comments**: Code explains itself; comments explain why. Prefer a name, a type, a small method, or a guard over a comment that says what the code does, and restructure before commenting. A comment is for what code cannot say: a constraint, a decision and its rejected alternative, a failure mode someone would reintroduce, a cross-repo or cross-language contract. XML `<summary>` is one sentence; `<remarks>` only when it carries such a why. Delete comments that narrate control flow or restate the next line.

## Git conventions

- Feature branches off `main`; PRs assigned to quabug; squash-merge, matching the history style.
- A PR that fixes an issue carries `Closes #NNN` (one line per issue) at the top of its body,
  and the commit message says it too, so merging closes the issue. Use `Towards #NNN` only for
  deliberately partial work, and when a second fix joins an existing PR, add its `Closes` line.
- Never commit or push without being asked.

## SDK

Requires .NET SDK 10.0.400+ (specified in `global.json` with `rollForward: latestMinor`). The
floor is the compiler, not a preference: the Roslyn analyzers this repo builds against are
compiled for 5.9.0.0, and an older SDK's `csc` refuses to load them with CS9057. `latestMinor`
rolls forward, never back, so an older SDK does not satisfy this and the build stops with a
version message rather than an analyzer one.

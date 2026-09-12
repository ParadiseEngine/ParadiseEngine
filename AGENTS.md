# ParadiseEngine agent guide

A .NET 10 / C# 14 game engine with ECS, WebGPU rendering, behavior trees and an asset pipeline.
This is the canonical agent guide; `CLAUDE.md` imports it.

Complete the requested behavior and relevant validation, including resolving failures caused
by the change. Continue through CI, release or merge work when the task includes it; report a
concrete blocker when completion depends on unavailable access or a user decision.

## Task-specific references

Use the relevant reference when changing its subsystem; unrelated tasks do not need these docs.
Paths mentioned inside the references are relative to this repository unless stated otherwise.

- Locks, queues and thread coordination: [concurrency](docs/development/concurrency.md).
- Behavior trees, blackboards and relative-pointer blobs: [BT and BLOB](docs/development/behavior-trees.md).
- Compute, GI, shader layouts or GPU profiling: [rendering contracts](docs/development/rendering.md).
- Host lifetime, snapshots or feature settings: [hosting and features](docs/development/hosting-features.md).
- Identity, importers, extraction, references or ozz animation: [assets and animation](docs/development/assets-animation.md).

## Build and test

Choose checks for the affected projects and behavior. The solution commands are available for
cross-project changes and CI parity; documentation-only changes need link and diff checks.

```bash
dotnet build ParadiseEngine.slnx
dotnet test --solution ParadiseEngine.slnx --output normal
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal
```

Use .NET SDK 10.0.400+ (`global.json`, `rollForward: latestMinor`). Older compilers cannot load
the Roslyn 5.9 analyzers (CS9057). Shared properties and package versions live in
`src/Directory.Build.props` and `src/Directory.Packages.props`.

Sample applications and their build/deployment CI live in
[ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples). Run sample commands
from that repository. Its `Paradise.BT.Sample` enables `PublishAot` to check tree construction and ticking. Tests allow
`Reflection.Emit` for the analyzer harness. BT serialization and BLOB's
`ManagedBlobAssetReference` still need dedicated AOT publish-and-run coverage.

## Coordinates

The data contract is **right-handed, Y-up, −Z forward, +X right**, in meters, with column-major
matrices. Consume Godot/glTF values without a Z mirror. ECS and rendering core are otherwise
coordinate-agnostic; conversions belong where transforms, projections or navmesh geometry form.

## Logging and filesystems

Libraries take `ILogger` and depend only on `Microsoft.Extensions.Logging.Abstractions`; hosts
choose providers. Use `[LoggerMessage]` on partial classes for checked, allocation-free disabled
logging. Logger parameters are non-nullable; use `NullLogger.Instance`. Pass `UPath` values intact
so hosts can render them through `ParadiseConsoleOptions.RenderValue`. Sinks must be thread-safe
for Dawn, Noesis and SDL callbacks. `Console` is reserved for program output such as verb results
and schema dumps, never library diagnostics.

Content readers take Zio `IFileSystem` and `UPath`, including pipeline/documents, `AuthoredDocument`
and Noesis XAML/texture/font providers. Hosts select archive, play-tree or memory mounts.

- Keep `/` separators verbatim. Let the mount enforce containment; confine untrusted relative
  URIs, such as GLB images, with `SubFileSystem` over the source directory.
- Use `MemoryFileSystem` in tests; mount disk fixtures relative to the test output directory.
- Wwise's native bank loader requires host paths; document equivalent exceptions explicitly.

## Style and Git

`.editorconfig` is enforced with warnings-as-errors: Allman braces, four spaces, file-scoped
namespaces, LF; `_camelCase` private/internal fields, `s_camelCase` statics and PascalCase constants
and public members. Prefer keyword types, omit `this.`, and preserve zero-allocation unmanaged
paths with structs, `ref` and `Unsafe` where required.

Return empty collections instead of null, using shared instances such as `AssetReferences.None`.
Null represents an absent single object. Comments explain constraints, decisions, failure modes
and cross-language contracts; delete control-flow narration. XML summaries are one sentence;
remarks hold rationale. Prefer clear names and small methods over explanatory prose.

Feature branches start from `main`; PRs go to quabug and squash-merge. Issue fixes put
`Closes #NNN` at the top of the PR body and in the commit message, one line per issue; use
`Towards #NNN` only for deliberately partial work. Commit and push only when requested or
already authorized by the task.
This is an independent repository; keep commits scoped to it. Use a separate worktree for
each implementation task and run Git/builds through physical paths. Do not stash a shared
checkout: another session may be editing it.

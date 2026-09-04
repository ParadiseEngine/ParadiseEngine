# Paradise Engine

A modular .NET game engine monorepo: archetype ECS, behavior trees, stateless physics
queries and rigid-body dynamics, a WebGPU (Dawn) renderer with a Slang shader pipeline
targeting desktop and the browser, glTF/KTX2 asset loading, an asset build pipeline driven
by the `paradise` CLI, SDL windowing, Wwise audio, and ImGui/NoesisGUI UI integrations.
Targets `net10.0`, C# 14, NativeAOT/trimming compatible.

All packages are published to NuGet from a single version tag — the libraries below plus
`Paradise.Cli`, which ships as a dotnet tool rather than a reference.

## Packages

### Core

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.BLOB](src/Paradise.BLOB) | [![NuGet](https://img.shields.io/nuget/v/Paradise.BLOB.svg)](https://www.nuget.org/packages/Paradise.BLOB) | Standalone unmanaged binary blob builder (BlobArray, BlobString, BlobPtr) |
| [Paradise.Physics](src/Paradise.Physics) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Physics.svg)](https://www.nuget.org/packages/Paradise.Physics) | Stateless collision queries (raycasts, shape casts) and rigid-body sphere dynamics (gravity, Coulomb friction, spin) |
| [Paradise.Export](src/Paradise.Export) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Export.svg)](https://www.nuget.org/packages/Paradise.Export) | Engine-neutral export core for editor hosts: exported-data contract, DotRecast navmesh baking, Blender/KTX tool orchestration |
| [Paradise.Authoring](src/Paradise.Authoring) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Authoring.svg)](https://www.nuget.org/packages/Paradise.Authoring) | Declare authoring data once with `[Authored]`; a source generator publishes an editor-neutral schema every editor builds its own UI from |

### ECS

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.ECS](src/Paradise.ECS) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.svg)](https://www.nuget.org/packages/Paradise.ECS) | Archetype-based ECS core; ships its source generator for queryables/systems |
| [Paradise.ECS.Tag](src/Paradise.ECS.Tag) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.Tag.svg)](https://www.nuget.org/packages/Paradise.ECS.Tag) | Zero-size tag component support |
| [Paradise.ECS.Concurrent](src/Paradise.ECS.Concurrent) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.Concurrent.svg)](https://www.nuget.org/packages/Paradise.ECS.Concurrent) | Concurrent command buffers and thread-safe structural changes |
| [Paradise.ECS.Jobs](src/Paradise.ECS.Jobs) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.Jobs.svg)](https://www.nuget.org/packages/Paradise.ECS.Jobs) | Parallel job scheduling over ECS chunks |

### Behavior trees

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.BT](src/Paradise.BT) | [![NuGet](https://img.shields.io/nuget/v/Paradise.BT.svg)](https://www.nuget.org/packages/Paradise.BT) | Behavior tree runtime (inspired by EntitiesBT); ships its source generator |
| [Paradise.BT.Builder](src/Paradise.BT.Builder) | [![NuGet](https://img.shields.io/nuget/v/Paradise.BT.Builder.svg)](https://www.nuget.org/packages/Paradise.BT.Builder) | Authoring DSL base classes |
| [Paradise.BT.Nodes](src/Paradise.BT.Nodes) | [![NuGet](https://img.shields.io/nuget/v/Paradise.BT.Nodes.svg)](https://www.nuget.org/packages/Paradise.BT.Nodes) | Built-in node library (Sequence, Selector, Parallel, decorators, delay) |

### Rendering

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Rendering](src/Paradise.Rendering) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.svg)](https://www.nuget.org/packages/Paradise.Rendering) | Backend-agnostic rendering data contract: handles, descriptors, reflection records |
| [Paradise.Rendering.WebGPU](src/Paradise.Rendering.WebGPU) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.WebGPU.svg)](https://www.nuget.org/packages/Paradise.Rendering.WebGPU) | WebGPU (Dawn) backend via WebGPUSharp |
| [Paradise.Rendering.Browser](src/Paradise.Rendering.Browser) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.Browser.svg)](https://www.nuget.org/packages/Paradise.Rendering.Browser) | Browser (WebAssembly) WebGPU backend driving the browser's own WebGPU through a bundled JS shim — consumers write no JavaScript |
| [Paradise.Rendering.Pbr](src/Paradise.Rendering.Pbr) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.Pbr.svg)](https://www.nuget.org/packages/Paradise.Rendering.Pbr) | PBR metallic-roughness scene renderer with embedded Slang-compiled shaders |

### Assets

Runtime readers — what a host links against to load what the pipeline built:

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Assets.Gltf](src/Paradise.Assets.Gltf) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Gltf.svg)](https://www.nuget.org/packages/Paradise.Assets.Gltf) | AOT-clean GLB/glTF 2.0 reader scoped to the Paradise export contract |
| [Paradise.Assets.Textures](src/Paradise.Assets.Textures) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Textures.svg)](https://www.nuget.org/packages/Paradise.Assets.Textures) | KTX2 texture transcoding (BasisLZ/UASTC) via libktx |

The build-time asset pipeline — authoring-side only; a host that merely mounts a built tree
never references these:

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Assets.Project](src/Paradise.Assets.Project) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Project.svg)](https://www.nuget.org/packages/Paradise.Assets.Project) | Asset project model: the `assets/` layout, the `project.toml` manifest, the content-addressed artifact cache shared with the Blender addon, and Zio mount construction |
| [Paradise.Assets.Documents](src/Paradise.Assets.Documents) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Documents.svg)](https://www.nuget.org/packages/Paradise.Assets.Documents) | Authored-document contracts: canonical TOML writing, `*.meta` sidecars, and the prefab/scene documents. C# reference implementation, mirrored in the Blender addon |
| [Paradise.Assets.Pipeline](src/Paradise.Assets.Pipeline) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Pipeline.svg)](https://www.nuget.org/packages/Paradise.Assets.Pipeline) | The build pipeline itself: source-tree verification, canonical-form checks, importers, and the build verbs' logic, on Zio |

### Windowing and audio

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Windowing](src/Paradise.Windowing) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Windowing.svg)](https://www.nuget.org/packages/Paradise.Windowing) | Backend-agnostic windowing contract: window control, render surfaces, timestamped raw device input |
| [Paradise.Windowing.Sdl](src/Paradise.Windowing.Sdl) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Windowing.Sdl.svg)](https://www.nuget.org/packages/Paradise.Windowing.Sdl) | SDL3 implementation of that contract, with WebGPU-ready surface descriptors for Win32/Cocoa/Wayland/X11 |
| [Paradise.Audio.Wwise](src/Paradise.Audio.Wwise) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Audio.Wwise.svg)](https://www.nuget.org/packages/Paradise.Audio.Wwise) | Audiokinetic Wwise integration; managed bindings only (requires a Wwise licence and a local SDK install, from which the native shim is built) |

### UI

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Ui](src/Paradise.Ui) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Ui.svg)](https://www.nuget.org/packages/Paradise.Ui) | Engine-neutral UI input contract: the `UiEvent` stream, the sim-thread `IUiInput` half, and `CompositeUiInput` fan-out for stacking UI systems |
| [Paradise.Ui.ImGui](src/Paradise.Ui.ImGui) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Ui.ImGui.svg)](https://www.nuget.org/packages/Paradise.Ui.ImGui) | Dear ImGui debug/overlay UI on the WebGPU backend |
| [Paradise.Ui.Noesis](src/Paradise.Ui.Noesis) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Ui.Noesis.svg)](https://www.nuget.org/packages/Paradise.Ui.Noesis) | NoesisGUI (XAML) integration (requires a NoesisGUI license) |

### Tools

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Cli.Host](src/Paradise.Cli.Host) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Cli.Host.svg)](https://www.nuget.org/packages/Paradise.Cli.Host) | The `paradise` command as a library: `BuildHost.Run(args, importers)`. What the tool runs, and what a game's own asset tool runs with its importers appended |
| [Paradise.Cli](src/Paradise.Cli) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Cli.svg)](https://www.nuget.org/packages/Paradise.Cli) | The `paradise` command: scaffold a project, verify and build its assets, and report on the build toolchain. Ships as a dotnet tool, not a library reference |

Source generators (`Paradise.ECS.Generators`, `Paradise.BT.Generators`,
`Paradise.Authoring.Generators`) are not published standalone — they ship inside
`Paradise.ECS`, `Paradise.BT` and `Paradise.Authoring` under `analyzers/dotnet/cs`, so
referencing those packages activates the codegen automatically.

## Monorepo layout

- `src/` — all library, test (`*.Test`), sample (`*.Sample`), generator, and benchmark projects.
- `src/Directory.Build.props` / `src/Directory.Packages.props` — shared build settings, shared
  NuGet package metadata, and centrally managed package versions.
- `src/Slang.targets` — Slang → WGSL shader toolchain (downloads a pinned `slangc` per
  `tools/slang/slang.manifest.json`, compiles and embeds shaders at build time).
- `src/Ktx.targets` — libktx native-library staging for platforms not covered by Ktx2.NET.
- `tools/slang/`, `tools/ktx/` — pinned external toolchains (manifest + bootstrap) for the
  Slang shader compiler and the `ktx create` CLI the texture step shells out to.
  `paradise tools doctor` reports both; `paradise tools install <ktx|slang>` fetches one.
- `ParadiseEngine.slnx` — top-level solution covering all projects.
- `AGENTS.md` — architecture notes, custom node patterns, and the coordinate convention. The
  canonical agent guide, shared across AI tools; `CLAUDE.md` imports it.

### Coordinate convention

Right-handed, **Y-up, −Z forward, +X right** (Godot / glTF standard), meters, column-major
matrices. Editor tools (`ParadiseGodotEditor`) export this data verbatim — no handedness
conversion anywhere in the pipeline.

## Build and test

```bash
dotnet build --solution ParadiseEngine.slnx
dotnet test --solution ParadiseEngine.slnx --output normal

# Single project
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal
```

Tests use TUnit on Microsoft.Testing.Platform. The first build of a shader-owning project
downloads the pinned Slang toolchain (cached under the NuGet package root).

## Asset projects

An asset project is an `assets/` source tree plus a `project.toml`, compiled into `build/`
by the `paradise` CLI. Install it globally, or pin it per repo in a tool manifest:

```bash
dotnet tool install --global Paradise.Cli
# or, per repo:  dotnet new tool-manifest && dotnet tool install Paradise.Cli
```

```bash
paradise new MyGame            # assets tree, a sample level, .gitignore
paradise assets verify         # sidecars, identities, validity
paradise assets verify --fix   # ... and repoint reference paths a rename left stale
paradise assets build          # assets/ -> build/  (--editor for .editor/play)
paradise assets watch          # keep *.meta in step, rebuilding as you go
paradise assets mv <from> <to> # move a file or directory; sidecars and every reference follow
paradise assets rm <path>      # delete an asset; refused while anything references it (--force to leave them dangling)
paradise assets refs <path>    # who references it, and what it references (--transitive)
paradise tools doctor          # every build tool: found, version, how to fix
```

Verbs are grouped (`paradise assets build`, not `paradise build`); `paradise --help` lists
them all with the shared `--project` and `--profile` options.

### A reference's guid decides; its path is a hint

An authored reference is `{ guid = "…", path = "…" }`. The **guid is the identity** and the
sidecars are the guid → path index the whole pipeline resolves through. The path is carried
because a guid alone is unreadable in a diff, and it is only ever a hint:

- Renaming a file in Finder or with `git mv` **never breaks a reference**. The sidecar travels
  with the file (or `watch` relinks the identity by content hash), so the guid still names it and
  the build resolves it. `verify` says so as a **warning**, and `verify --fix` catches the path up.
- A guid **no asset carries** is an error: the reference names nothing, and no path can stand in
  for an identity that is gone. Restore the asset or its sidecar, or repoint the reference.
- When the two halves disagree — the path names a different asset than the guid — **the guid
  wins**. Resolving by path would silently repoint every reference at the wrong asset the first
  time two filenames were swapped.

`paradise assets mv` still rewrites eagerly, because a tree whose paths are true is the one worth
committing. It is the tidy path, not the load-bearing one. `watch` does the same after a rename it
sees, so a Finder rename leaves the tree as tidy as `mv` would — and when a delete outlives the
30 s the identity is held for, it names every reference left dangling.

**A mesh names its textures the same way, in its sidecar.** A container's external uris are
resolved once and recorded under `[mesh]` in the mesh's `.meta` as `{ slot, uri, guid, path }`
entries; the uri is what the DCC follows, the recorded guid is what the pipeline follows. The
container is only ever READ, so an FBX gets the same story as a GLB — the resolution lives in
tooling-owned import settings, the way an FBX importer records its texture remaps. `verify --fix`
and `watch` record what is missing, a texture rename catches the entry (and, for a format that
can be written, the uri) up instead of forcing a re-export, and a uri that changed since it was
recorded is a re-export and is re-resolved from scratch.

**Who references what** is answered by `ReferenceGraph`, built per run from the sidecars and the
documents — never stored in a sidecar, which would be a second copy of the document kept in sync
by a watcher that may not be running. `mv` rewrites only the dependents of what moved, `rm` refuses
what is still referenced, and `refs` prints both directions.

A game that needs its own asset kind writes an `IAssetImporter` (it claims or declines inside
`Import`) and runs the same verbs through `Paradise.Cli.Host` from a console project of its own —
the tool cannot be handed code, and NativeAOT rules out scanning for it:

```csharp
// tools/assets/Program.cs — `dotnet run --project tools/assets -- assets build`
return Paradise.Cli.BuildHost.Run(args, [.. AssetImporters.All, new MyBankImporter()]);
```

The chain is lowest precedence first, so an appended importer shadows the built-in it replaces.

An importer that wants its asset kind in the reference graph — and so followed by `mv`, guarded by
`rm`, listed by `refs`, checked by `verify` and caught up by `watch` — implements two more methods:
`References` (every site the asset holds, from its bytes and its sidecar; null to decline) and
`Rewrite` (bring them in line with the tree: the sidecar's entries always, the asset's own bytes
only when the context allows). The findings are derived from the sites by the one rule, so an
importer cannot forget one; nothing in the pipeline lists formats.

## Releasing

Pushing a `v*` tag (or manually dispatching the *Publish NuGet packages* workflow with a
version) packs all library projects at that version and pushes them to nuget.org via OIDC
trusted publishing:

```bash
git tag v0.2.0
git push origin v0.2.0
```

## Package-specific notes

- `src/Paradise.BLOB/README.md` — blob builders and serialization format
- `src/Paradise.BT/README.md` — behavior tree pipeline, custom nodes, serialization
- `src/Paradise.Physics/README.md` — collision world and query semantics

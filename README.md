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
| [Paradise.Features](src/Paradise.Features) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Features.svg)](https://www.nuget.org/packages/Paradise.Features) | Feature switches layered from configuration, environment and CLI |
| [Paradise.Features.Toml](src/Paradise.Features.Toml) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Features.Toml.svg)](https://www.nuget.org/packages/Paradise.Features.Toml) | TOML configuration reader, isolated from switch-only consumers |
| [Paradise.Hosting](src/Paradise.Hosting) | — | Application lifetime, feature configuration, fixed-step loops, capture scheduling and pooled snapshot transport |
| [Paradise.Hosting.Desktop](src/Paradise.Hosting.Desktop) | — | SDL/offscreen platform adapters for application hosting |
| [Paradise.Physics](src/Paradise.Physics) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Physics.svg)](https://www.nuget.org/packages/Paradise.Physics) | Stateless collision queries (raycasts, shape casts) and rigid-body sphere dynamics (gravity, Coulomb friction, spin) |
| [Paradise.Export](src/Paradise.Export) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Export.svg)](https://www.nuget.org/packages/Paradise.Export) | Engine-neutral export core for editor hosts: exported-data contract, DotRecast navmesh baking, Blender/KTX tool orchestration |
| [Paradise.Authoring](src/Paradise.Authoring) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Authoring.svg)](https://www.nuget.org/packages/Paradise.Authoring) | `[Authored]` records and generated editor schemas |

### ECS

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.ECS](src/Paradise.ECS) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.svg)](https://www.nuget.org/packages/Paradise.ECS) | Archetype-based ECS core; ships its source generator for queryables/systems |
| [Paradise.ECS.Tag](src/Paradise.ECS.Tag) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.Tag.svg)](https://www.nuget.org/packages/Paradise.ECS.Tag) | Zero-size tag component support |
| [Paradise.ECS.Managed](src/Paradise.ECS.Managed) | [![NuGet](https://img.shields.io/nuget/v/Paradise.ECS.Managed.svg)](https://www.nuget.org/packages/Paradise.ECS.Managed) | Managed class components with explicit snapshot policies, deferred commands and generated queries/systems |
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
| [Paradise.Rendering.Browser](src/Paradise.Rendering.Browser) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.Browser.svg)](https://www.nuget.org/packages/Paradise.Rendering.Browser) | WebAssembly WebGPU backend with a bundled JavaScript bridge |
| [Paradise.Geometry](src/Paradise.Geometry) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Geometry.svg)](https://www.nuget.org/packages/Paradise.Geometry) | Wide quantized BVHs with CPU construction and reference traversal |
| [Paradise.Rendering.Pbr](src/Paradise.Rendering.Pbr) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.Pbr.svg)](https://www.nuget.org/packages/Paradise.Rendering.Pbr) | PBR metallic-roughness scene renderer with embedded Slang-compiled shaders, Forward+ lights, shadow maps, and runtime probe global illumination over a compute ray tracer |

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
| [Paradise.Assets.Project](src/Paradise.Assets.Project) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Project.svg)](https://www.nuget.org/packages/Paradise.Assets.Project) | Project layout, manifest, shared artifact cache and Zio mounts |
| [Paradise.Assets.Documents](src/Paradise.Assets.Documents) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Documents.svg)](https://www.nuget.org/packages/Paradise.Assets.Documents) | Canonical TOML, sidecars and prefab documents, mirrored in the Blender addon |
| [Paradise.Assets.Pipeline](src/Paradise.Assets.Pipeline) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Pipeline.svg)](https://www.nuget.org/packages/Paradise.Assets.Pipeline) | Verification, importers and asset build operations on Zio |

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
| [Paradise.Cli.Host](src/Paradise.Cli.Host) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Cli.Host.svg)](https://www.nuget.org/packages/Paradise.Cli.Host) | CLI library and custom-importer entry point: `BuildHost.Run(args, importers)` |
| [Paradise.Cli](src/Paradise.Cli) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Cli.svg)](https://www.nuget.org/packages/Paradise.Cli) | The `paradise` dotnet tool for project, asset and toolchain operations |

Source generators (`Paradise.ECS.Generators`, `Paradise.BT.Generators`,
`Paradise.Authoring.Generators`) are not published standalone — they ship inside
`Paradise.ECS`, `Paradise.BT` and `Paradise.Authoring` under `analyzers/dotnet/cs`, so
referencing those packages activates the codegen automatically.

## Monorepo layout

- `src/` — library, test (`*.Test`), generator, and benchmark projects.
- [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples) — native and browser sample applications, their NativeAOT smoke test, and website deployment. [Run the browser demos](https://paradiseengine.dev/samples/).
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
dotnet build ParadiseEngine.slnx
dotnet test --solution ParadiseEngine.slnx --output normal

# Single project
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal
```

Tests use TUnit on Microsoft.Testing.Platform. The first build of a shader-owning project
downloads the pinned Slang toolchain (cached under the NuGet package root).

## Asset projects

The `paradise` CLI builds an `assets/` tree and its `project.toml` into `build/`.
Install globally, or use a repository tool manifest:

```bash
dotnet tool install --global Paradise.Cli
# Per repository: dotnet new tool-manifest && dotnet tool install Paradise.Cli
```

```bash
paradise new MyGame                    # scaffold an asset project
paradise assets verify                 # validate sidecars, identities and documents
paradise assets verify --fix           # repair stale reference paths
paradise assets build                  # assets/ -> build/; --editor uses .editor/play/
paradise assets watch                  # maintain sidecars and rebuild
paradise assets mv <from> <to>          # move assets, sidecars and reference hints
paradise assets rm <path>              # refuse referenced assets unless --force
paradise assets refs <path>            # references in both directions; --transitive recurses
paradise assets extract <glb>          # extract parts; --all processes a directory
paradise host play --scene assets/levels/arena.prefab
paradise host play --watch             # run through dotnet watch
paradise host build                    # build the launcher
paradise tools doctor                  # tool versions and installation help
```

Use `paradise --help` for verbs and shared `--project` / `--profile` options.

### Running a game

`[host]` names the launcher relative to the project root and arguments placed before caller arguments:

```toml
[host]
project = "Game.Launcher/Game.Launcher.csproj"
arguments = ["--ui", "ui/Shell.xaml"]
```

`host play` builds assets into `.editor/play/`, updates the launcher, runs the scene's built path
and waits for exit. Its exit code matches the game; SIGTERM or Ctrl+C stops the process tree.
`--no-build` reuses binaries, `--no-assets` skips assets, `-c Release` selects configuration,
and arguments after `--` go to the game.

Freshness checks use the reference closure in `obj/project.assets.json`, including injected
ProjectReferences. Newer source/project files trigger MSBuild; restore runs only when project files
changed. Unchanged inputs skip it; the first check after an external build requires a no-op pass.

`--watch` uses `dotnet watch run --non-interactive`. Method edits hot-patch; other changes may
restart. Hot Reload does not rerun constructors or static initializers: force a restart, such as
by changing a signature, for those edits. Launchers enabling `PublishAot` need
`StartupHookSupport=true` in Debug for the watch agent.

### GLB extraction

`paradise assets extract Models/crate.glb` creates mesh, skeleton and clip reference documents,
material documents, external images and a prefab. The GLB remains source: build cooks `.mesh` /
`.skinnedmesh` to Paradise blobs and `.skeleton` / `.anim` to ozz archives. A skinned mesh names
its skeleton. Runtime consumers load cooked files and built materials. Clips retain keys unless
the GLB sidecar enables `[glb] optimize = { tolerance = 0.001, distance = 0.1 }`.

Routes are assets-relative:

```toml
[extract]
directory  = "models"          # common fallback
meshes     = "models"          # .mesh / .skinnedmesh
skeletons  = "animations"      # defaults to the meshes route
animations = "animations"      # .anim
materials  = "materials"
textures   = "textures"
prefabs    = "prefabs/models"
tilesets   = "tilesets"        # a kind declared by a game importer
```

Without routes, outputs stay beside the container. Kind-specific fallbacks precede `directory`;
a per-GLB `[glb] extract` folder overrides all routes. Routing affects new files only: recorded
outputs retain identity and location. Move them explicitly with `assets mv`.

Watchers freely mint/update tool-owned mesh, skeleton and clip documents. Materials, images and
the generated prefab become authored files. Extraction tracks container and document fingerprints:
re-exports update materials while retaining Paradise-only fields, material edits can update the
GLB's glTF fields, and image edits are reported. Changes on both sides require `--take-glb` or
`--take-document`. The prefab is created once and never synchronized. KTX2 is build output;
`verify` rejects authored KTX2 beneath `assets/`.

### References and sidecars

An authored reference is `{ guid = "…", path = "…" }`. **GUID identifies; path is a readable hint.**
`AssetIndex` resolves identity through sidecars. Stale hints are warnings repaired by `verify --fix`;
a missing identity is an error even if the hinted path exists. Renames preserve references when
the sidecar travels or the watcher relinks it by content hash. The watcher holds deleted identities
for 30 seconds, then reports remaining dangling references.

Container texture URIs use `[mesh]` sidecar entries `{ slot, uri, guid, path }`. The DCC follows
the URI; the pipeline follows the GUID. `verify --fix` and `watch` record missing entries and update stale URIs
when the format supports rewriting. A changed source URI is treated as a re-export and resolved
again. `ReferenceGraph` derives edges per run from documents and sidecars; it is never persisted.
Moves follow dependents, removal protects referenced assets, and `refs` lists both directions.

Sidecar creation records `importer = "mesh"` using `Claims`, with appended importers taking
precedence. All later verbs honor that name. Change the line to choose another importer for an
existing asset; changing chain order affects newly claimed assets. Unknown names are errors,
missing names are repairable warnings, and a named importer declining an asset fails its build.

### Custom importers

Implement `IAssetImporter`: `Claims` examines the path and at most a header; `Import` builds it.
The global CLI can load public, parameterless importer classes from prebuilt assemblies:

```toml
[extensions]
assemblies = ["tools/assets/bin/Debug/net10.0/MyGame.Assets.dll"]
```

Paths are project-root-relative. Importers append to the same chain used by every verb, including
watch and host play. Dynamic loading requires compatible Paradise versions; the CLI therefore
is not trimmed or NativeAOT-published. For CI, a game-owned console tool gives MSBuild control
of the complete dependency graph:

```csharp
return Paradise.Cli.BuildHost.Run(args, [.. AssetImporters.All, new MyBankImporter()]);
```

Implement `References` to expose sites and `Rewrite` to repair them; source bytes may change only
when the context permits. This integrates the kind with build, verify, watch, move, remove and refs.

Extraction uses the same importer and recorded name. Declare output kinds to enable it:

```csharp
public string Name => "crate";
public bool Claims(ImportCandidate candidate) => candidate.Asset.GetExtensionWithDot() == ".crate";
public IReadOnlyList<ExtractKindDeclaration> ExtractKinds { get; } =
[
    new("tilesets"),
    new(ExtractKind.Materials),
];
```

`HasParts`, `HasAuthoredParts`, `IsExtracted`, `Extract` and `MintReferences` have defaults for
non-container importers. Verify rejects routes for undeclared kinds. Call extraction only from
extract/watch: `ImportContext.FileSystem` is read-only, and writing build inputs would invalidate
the incremental index.

Shared extraction APIs handle these contracts:

- `ExtractionRecord` stores kind, ownership, index, name, identity and two fingerprints in the
  `[extract]` sidecar domain.
- `ToolOwned` parts follow the container; `TwoSided` documents can change on either side;
  `Blob` parts are authored bytes with no write-back operation.
- `ExtractionSync.Decide` classifies changes and conflicts. After `TakeDocument`, fingerprints
  agree only if the importer can write the edit back.
- Generated prefabs are unrecorded; validate their route immediately after writing.
- `SidecarMaintainer.Ensure` mints identity; `AssetIndex.Resolve` finds moved outputs by GUID.
  Update recorded path hints when resolving them.

See `GameExtractorTests` for a complete public-API example with routing, conflicts and moves.

## Third-party libraries

Versions are centrally managed in `src/Directory.Packages.props`; see that file for the
reasoning behind each pin.

| Library | License | Used for |
| --- | --- | --- |
| [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp) (Roslyn) | MIT | Source generators (ECS, BT, Authoring) and their tests |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) | MIT | The engine's logging contract; hosts choose the sink |
| [System.Runtime.CompilerServices.Unsafe](https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe) | MIT | Low-level unmanaged/blob code |
| [System.Text.Json](https://www.nuget.org/packages/System.Text.Json) | MIT | JSON reading/writing in the asset pipeline |
| [WebGPUSharp](https://www.nuget.org/packages/WebGPUSharp) | MIT | Dawn/WebGPU bindings for `Paradise.Rendering.WebGPU` |
| [Noesis.GUI](https://www.nuget.org/packages/Noesis.GUI) | Commercial (requires a NoesisGUI licence) | NoesisGUI (XAML) player-facing UI, `Paradise.Ui.Noesis` |
| [Hexa.NET.ImGui](https://www.nuget.org/packages/Hexa.NET.ImGui) | MIT | Dear ImGui binding for debug/dev tooling, `Paradise.Ui.ImGui` |
| [Hexa.NET.ImGuizmo](https://www.nuget.org/packages/Hexa.NET.ImGuizmo) | MIT | Transform gizmos for the editor's Scene panel |
| [ppy.SDL3-CS](https://www.nuget.org/packages/ppy.SDL3-CS) | MIT | SDL3 windowing/input, `Paradise.Windowing.Sdl` |
| [Ktx2.NET](https://www.nuget.org/packages/Ktx2.NET) | Apache-2.0 (wraps libktx) | KTX2 texture transcoding, `Paradise.Assets.Textures` |
| [Zio](https://www.nuget.org/packages/Zio) | BSD-2-Clause | Filesystem abstraction every asset path goes through |
| [Tomlyn](https://www.nuget.org/packages/Tomlyn) | BSD-2-Clause | TOML reading for authored documents and `project.toml` |
| [DotRecast.Core / .Detour / .Recast](https://www.nuget.org/packages/DotRecast.Core) | zlib | Navmesh baking in `Paradise.Export` |
| [ozz-animation](https://github.com/guillaumeblanc/ozz-animation) archive format | MIT | `Paradise.Animation` is a managed port reading/writing ozz's v2/v7 archives (no native dependency) |
| [TUnit](https://www.nuget.org/packages/TUnit) | MIT | Test framework (Microsoft.Testing.Platform) |
| [Microsoft.Coyote.Test](https://www.nuget.org/packages/Microsoft.Coyote.Test) | MIT | Systematic concurrency testing (`*.CoyoteTest` projects) |
| [BenchmarkDotNet](https://www.nuget.org/packages/BenchmarkDotNet) | MIT | Benchmarking (e.g. `Paradise.Animation.Benchmarks`) |

Not on NuGet, vendored/downloaded by the build itself:

| Tool | License | Used for |
| --- | --- | --- |
| Slang (`slangc`) | Apache-2.0 WITH LLVM-exception | Slang → WGSL shader compilation, via `src/Slang.targets` |
| libktx (`ktx` CLI) | Apache-2.0 | KTX2 texture creation, via `src/Ktx.targets` |
| Audiokinetic Wwise | Commercial (requires a Wwise licence) | Native audio engine behind `Paradise.Audio.Wwise` (requires a licensed local SDK) |

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

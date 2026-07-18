# Paradise Engine

A modular .NET game engine monorepo: archetype ECS, behavior trees, stateless physics
queries, a WebGPU (Dawn) renderer with a Slang shader pipeline, glTF/KTX2 asset loading,
and ImGui/NoesisGUI UI integrations. Targets `net10.0`, C# 14, NativeAOT/trimming
compatible throughout.

All library packages are published to NuGet from a single version tag.

## Packages

### Core

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.BLOB](src/Paradise.BLOB) | [![NuGet](https://img.shields.io/nuget/v/Paradise.BLOB.svg)](https://www.nuget.org/packages/Paradise.BLOB) | Standalone unmanaged binary blob builder (BlobArray, BlobString, BlobPtr) |
| [Paradise.Physics](src/Paradise.Physics) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Physics.svg)](https://www.nuget.org/packages/Paradise.Physics) | Stateless collision queries: static colliders, raycasts, shape casts |

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

### Rendering and assets

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Rendering](src/Paradise.Rendering) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.svg)](https://www.nuget.org/packages/Paradise.Rendering) | Backend-agnostic rendering data contract: handles, descriptors, reflection records |
| [Paradise.Rendering.WebGPU](src/Paradise.Rendering.WebGPU) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.WebGPU.svg)](https://www.nuget.org/packages/Paradise.Rendering.WebGPU) | WebGPU (Dawn) backend via WebGPUSharp |
| [Paradise.Rendering.Pbr](src/Paradise.Rendering.Pbr) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Rendering.Pbr.svg)](https://www.nuget.org/packages/Paradise.Rendering.Pbr) | PBR metallic-roughness scene renderer with embedded Slang-compiled shaders |
| [Paradise.Assets.Gltf](src/Paradise.Assets.Gltf) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Gltf.svg)](https://www.nuget.org/packages/Paradise.Assets.Gltf) | AOT-clean GLB/glTF 2.0 reader scoped to the Paradise export contract |
| [Paradise.Assets.Textures](src/Paradise.Assets.Textures) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Assets.Textures.svg)](https://www.nuget.org/packages/Paradise.Assets.Textures) | KTX2 texture transcoding (BasisLZ/UASTC) via libktx |

### UI

| Package | NuGet | Description |
| --- | --- | --- |
| [Paradise.Ui.ImGui](src/Paradise.Ui.ImGui) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Ui.ImGui.svg)](https://www.nuget.org/packages/Paradise.Ui.ImGui) | Dear ImGui debug/overlay UI on the WebGPU backend |
| [Paradise.Ui.Noesis](src/Paradise.Ui.Noesis) | [![NuGet](https://img.shields.io/nuget/v/Paradise.Ui.Noesis.svg)](https://www.nuget.org/packages/Paradise.Ui.Noesis) | NoesisGUI (XAML) integration (requires a NoesisGUI license) |

Source generators (`Paradise.ECS.Generators`, `Paradise.BT.Generators`) are not published
standalone — they ship inside `Paradise.ECS` and `Paradise.BT` under `analyzers/dotnet/cs`,
so referencing those packages activates the codegen automatically.

## Monorepo layout

- `src/` — all library, test (`*.Test`), sample (`*.Sample`), generator, and benchmark projects.
- `src/Directory.Build.props` / `src/Directory.Packages.props` — shared build settings, shared
  NuGet package metadata, and centrally managed package versions.
- `src/Slang.targets` — Slang → WGSL shader toolchain (downloads a pinned `slangc` per
  `tools/slang/slang.manifest.json`, compiles and embeds shaders at build time).
- `src/Ktx.targets` — libktx native-library staging for platforms not covered by Ktx2.NET.
- `ParadiseEngine.slnx` — top-level solution covering all projects.
- `CLAUDE.md` — architecture notes, custom node patterns, and the coordinate convention.

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

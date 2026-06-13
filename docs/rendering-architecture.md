# Paradise.Rendering Architecture

> Last updated: 2026-06-12

## Overview

`Paradise.Rendering.WebGPU` is the first concrete backend for the `Paradise.Rendering` data-contract layer. It targets [WebGPU](https://www.w3.org/TR/webgpu/) via [Dawn](https://dawn.googlesource.com/dawn) (Chromium's native WebGPU implementation), and exposes a minimal, allocation-light API suitable for Native AOT compilation.

The stack looks like this:

```
Paradise.Rendering (data contracts: PipelineDesc, RenderCommandStream, …)
         ↓
Paradise.Rendering.WebGPU (WebGpuRenderer, ShaderProgramLoader, …)
         ↓
WebGpuSharp (thin P/Invoke wrappers around Dawn's webgpu.h)
         ↓
Dawn native library (libdawn_proc / libwebgpu_dawn)
```

---

## Key Subsystems

### Handle Layer (`SlotTable<T>`, `ShaderHandle`, `PipelineHandle`)

All GPU-owned resources are exposed through **generation-tracked handles** rather than direct pointers or reference types.

- `SlotTable<T>` allocates slots with `(Index, Generation)` pairs. Generation starts at 1 (0 is the invalid sentinel). When a slot is freed, the generation increments; any stale handle with the old generation fails a synchronous `InvalidOperationException` on next access.
- `ShaderHandle` and `PipelineHandle` wrap `(uint Index, uint Generation)`. `IsValid` is `Generation != 0`.
- Stale-handle detection is synchronous and cheap: a single array bounds check plus a generation compare.

**Why generation handles?** They eliminate use-after-free bugs across renderer rebuilds and resize events without requiring reference counting or GC pressure. A destroyed shader does not pull a live pipeline out from under the caller — the pipeline cache holds a strong reference to the native object independently.

### Pipeline Cache (`PipelineCache`)

`PipelineCache` sits below the public `PipelineHandle` layer. Its responsibilities:

1. **Deduplication**: two `CreatePipeline` calls with structurally-equal `PipelineDesc`s share one native `WgRenderPipeline`. Equality is deep-structural (entry point strings, vertex layouts element-by-element, format, topology, layout shape).
2. **Ownership**: the cache owns the native pipeline for the renderer's lifetime. Handle destruction does not destroy the underlying native object; the cache keeps it alive so a second handle to the same pipeline is still valid after the first is destroyed.
3. **Eviction policy (M1)**: unbounded. Entries are retained until `WebGpuRenderer.Dispose`. This is intentional for M1: the pipeline set is small and known at build time; a future milestone (M2/M3) can add LRU or refcount-based eviction when dynamic pipeline rebuilds land.

`PipelineDesc.ContentHash()` is a 32-bit hash over all structural fields (shader handles, entry point strings, vertex layouts, topology, format, layout). The `Name` field is excluded — it is a debug label only.

### Deferred Destruction (`DeferredDestructionQueue`)

GPU commands execute asynchronously. Destroying a native resource while in-flight commands reference it causes undefined behavior. `DeferredDestructionQueue` delays native teardown by N frames (default: 2, covering the typical Dawn in-flight frame count).

- `Enqueue(resource)` adds to the current frame's bucket.
- `Tick()` advances the frame index and releases the bucket that is now safe to destroy.
- Called once per `Submit()` in the render loop.

### Shader Compilation Toolchain (`Slang.targets`)

Shaders are written in [Slang](https://shader-slang.com/) and compiled to WGSL at **build time** via `slangc`. The MSBuild target (`src/Slang.targets`) orchestrates:

1. **`RestoreSlang`**: downloads and verifies `slangc` from the manifest (`tools/slang/slang.manifest.json`) into a per-version RID cache (`~/.nuget/packages/_slang/<version>/<rid>/`).
2. **`CompileSlangShaders`**: for each `Shaders/**/*.slang` source, runs `slangc -target wgsl -o <name>.wgsl -reflection-json <name>.reflection.json`.
3. **`_AddSlangEmbeddedResources`**: promotes the outputs as `EmbeddedResource` items with logical names `Shaders.<name>.wgsl`, `Shaders.<name>.reflection.json`, and `Shaders.<name>.raw-reflection.json`.

`ShaderProgramLoader.Load(assembly, prefix)` reads these embedded resources at runtime, parses the reflection JSON, and returns a `ShaderProgramDesc` ready for `WebGpuRenderer.CreatePipeline`.

### Surface Factory (`SurfaceFactory`)

`SurfaceFactory.Create` dispatches to the OS-appropriate Dawn surface constructor based on `SurfaceDescriptor.Platform`:

| Platform | Source handle(s) | Dawn API |
|---|---|---|
| `Win32` | HWND | `CreateSurfaceFromWindowsHWND` |
| `Xlib` | Display*, Window | `CreateSurfaceFromXlibWindow` |
| `Wayland` | wl_display*, wl_surface* | `CreateSurfaceFromWaylandSurface` |
| `Cocoa` | CAMetalLayer* | `CreateSurfaceFromMetalLayer` |
| `Headless` | — | offscreen texture (no surface) |

The `Headless` platform is an internal concept: `WebGpuRenderer.CreateHeadless(w, h)` skips surface creation and renders to an offscreen `WgTexture`. This is the path used in CI and tests.

---

## Milestone History

| Milestone | Description |
|---|---|
| M0b | Dawn initialization, clear-color headless render, SurfaceFactory |
| M1 | Triangle draw path: Slang→WGSL toolchain, ShaderProgramLoader, PipelineDesc, RenderCommandStream |
| M4 | Stabilization: generation handle tests, pipeline cache tests, resize/device-loss coverage, Slang regression snapshot suite |

---

## Testing Strategy

Tests live in `src/Paradise.Rendering.WebGPU.Test/` and use [TUnit](https://tunit.dev/).

- **CPU-only tests** (no GPU required): handle generation/stale-detection, `PipelineDesc.ContentHash()` collision resistance, `PipelineDesc` structural equality, `SurfaceDescriptor` null-handle guard logic, Slang reflection snapshot normalization.
- **GPU-backed tests** (require Dawn headless adapter): `WebGpuRenderer.CreateHeadless` → `CreateShader` → `Submit` round-trips, pipeline cache deduplication under destroy. Tests skip cleanly via `AdapterUnavailableException` or `DllNotFoundException` when Dawn is not available (e.g., CI runners without Vulkan drivers).
- **Slang regression snapshots** (`SlangRegressionTests`): pin both the raw `slangc` reflection JSON schema and the `ShaderProgramLoader` output structure. Run `SLANG_UPDATE_SNAPSHOTS=1 dotnet test` to regenerate golden fixtures after a Slang version bump, then review and commit the diff in `fixtures/`.
- **Resize coverage** (`ResizeTests`): multi-step resize sequences, zero-size clamping, post-dispose throw.
- **Surface mapping** (`SurfaceMappingTests`): null-handle rejection per platform, current-OS platform mapping documentation.

### CI

- **`.github/workflows/dotnet-test.yml`**: runs the full test suite on every push/PR. Does not install mesa-vulkan-drivers; GPU tests skip gracefully.
- **`.github/workflows/rendering-smoke.yml`**: path-filtered to rendering source, installs `mesa-vulkan-drivers`, runs the WebGPU test project with GPU-backed tests enabled, and exercises the sample binary in `--headless 10` mode.

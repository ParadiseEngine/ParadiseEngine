# Managed allocation during a game run

Measured with [`Paradise.Runtime.Benchmarks`](../../src/Runtime/Paradise.Runtime.Benchmarks/README.md),
which drives the reference game's composition through the real hosts and attributes every byte to a
phase, a renderer stage or a built-in feature. Steady state means after 300 warm-up frames.

Machine: .NET 11.0.0, x64 Linux (WSL2, 12 threads), workstation concurrent GC. WebGPU runs Dawn on
the `dzn` Vulkan-on-D3D12 driver. The gen0 budget here was about 11 MiB: 44 MiB allocated triggered
four gen0 collections.

## Results

| Scenario | Bytes/frame | Where |
| --- | ---: | --- |
| Reference game (9 cubes, 1 light), null backend, cooperative host | **0** | all 113 phases and segments allocation-free |
| Showcase (111 instances, 23 lights, GI, RTAO, SSR, fog, decals, full post: 41 live passes, 2260 commands), null backend | **0** | all 113 phases and segments allocation-free |
| Reference game, null backend, threaded host (60 Hz) | **0** | one-off 120 B and 40 B while the snapshot pool reached its steady size |
| Reference or showcase, WebGPU backend (offscreen) | **128** | `upload + backend submit`: see W1 and W2 |
| Churn: 16 of 256 entities replaced per tick | **2304** | `extract`: 144 B per new renderable in `PbrSceneBuilder` (S1); ECS despawn and spawn allocate nothing |
| Showcase animating 2 lights, DoF focus and exposure with `with` (as the samples do) | **312** | the game's own `with` copies: 2 `PbrLight`, `PbrDepthOfField`, `PbrExposure` (A1) |

No scenario collected even once in its measured window, except churn, which collected gen0 every
5000 frames (83 s at 60 frames/s). The renderer, frame graph, ECS queries, snapshot copies,
`SnapshotStream`, `AssetLoader.Update` and both hosts allocate nothing per frame. The goal is
therefore to keep frames at zero, close the few sources a real game hits, and stop regressions.

Over 60,000 WebGPU frames (17 minutes at 60 frames/s) no gen0 collection ran, so 60,000
finalizable texture-view wrappers stayed queued until the window forced a collection (60,041 live
view handles, back to 41 afterwards). Rare collections are what lets finalizer-owned wrappers pile
up. Native growth was not observed: a separate run creating a million views of one texture kept
the working set flat even without releasing them, because Dawn returns a cached view for a repeated
descriptor. Deterministic release still matters for the swapchain path, which views a new texture
every frame and could not be measured headless.

## Sources and fixes

### W1. Backbuffer view created every frame (WebGPU)

`OffscreenTarget.TryAcquireView` and `SurfaceTarget.TryAcquireView` call `Texture.CreateView()`
every frame. WebGPUSharp returns a `TextureView` class holding a `WebGpuSafeHandle`, which has a
finalizer and no public `Dispose` on the wrapper. The surface path also wraps the swapchain texture
from `Surface.GetCurrentTexture()` in a second wrapper and handle each frame. By inspection, its
`FromHandle` adds a reference to the one `GetCurrentTexture` already returned; verify on a
presenting device.

- Offscreen: create the view once per texture, in the constructor and `Resize`, and return it.
- Surface: take the raw path, which WebGPUSharp 0.5.8 exposes publicly:
  `SurfaceHandle.GetCurrentTexture(ref SurfaceTextureFFI)`, `TextureHandle.CreateView()` and
  `Release()` of both after `Present`. The backbuffer attachment is then passed as a
  `TextureViewHandle` through `CommandEncoderHandle.BeginRenderPass(in RenderPassDescriptorFFI)`,
  so `ExecuteStream` needs a raw form of its backbuffer pass begin.
- Alternatively, ask upstream to expose `Dispose` on `WebGPUManagedHandleBase` and dispose both
  wrappers after `Present`. That keeps four small objects per frame but makes release
  deterministic and finalizer-free.

Validation: with `--backend webgpu`, live view handles stay constant over the window, and bytes per
frame drop by the view's share. Add a GPU-lane test asserting
`WebGpuSafeHandle.GetActiveHandles<TextureViewHandle>()` is unchanged across 100 frames.

### W2. Frame pacing wait allocates a closure (WebGPU, offscreen)

`FramePacer` calls `Queue.OnSubmittedWorkSync` before each submission to an offscreen target.
WebGPUSharp builds an `Action` over a closure and pins it with a `GCHandle` on every call. Replace
it with `WebGPU_FFI.QueueOnSubmittedWorkDone` using a static `[UnmanagedCallersOnly]` callback with
`CallbackMode.WaitAnyOnly`, plus `WebGPU_FFI.InstanceWaitAny`; both are public. A surface target
paces itself and never takes this path, so this affects headless runs, CI and captures.

Validation: W1 and W2 together bring `--backend webgpu` to 0 bytes per frame.

### S1. A new renderable allocates its instance (`PbrSceneBuilder`)

Each new key allocates a `PbrInstance` (about 120 B) and a `Slot` (24 B); keys that vanish are
dropped for the GC. A game keying renderables by entity identity pays this per spawn: 144 KiB/s at
1000 spawns per second.

- Store `Slot` as a struct in the dictionary (`CollectionsMarshal.GetValueRefOrAddDefault`), which
  removes the 24 B.
- Recycle instances per mesh: `End` pushes a removed key's instance onto a free list for its
  `PbrMesh` (`Mesh` is init-only), and `Add` for a new key pops one and resets every mutable field
  to its default (`Model`, `GiMesh`, `Highlight`, `ReceivesDecals`, `JointOffset`, `GiMode`,
  `ShadowsOnly`).
- Safe for motion vectors: `MotionHistory` keys history by instance reference, but rebuilds it
  every frame from that frame's drawn instances. An instance removed in `End` was not drawn that
  frame, so it carries no history into its next use. No other renderer state is keyed by instance.
  Pin this with a test: a recycled instance's first frame reports no previous transform.
- Bound the free lists (for example by the peak live count per mesh) and drop a mesh's list when
  the mesh is released, so pools never keep unloaded geometry alive.

Validation: `--entities 256 --churn 16 --budget 0` passes.

### A1. Scene settings that can only be replaced

`PbrLight` is a record with settable properties, so animating one in place costs nothing; the
samples' `light with { … }` copies it every frame instead. The post and lighting settings
(`PbrDepthOfField`, `PbrExposure`, `PbrFog`, `PbrBloom`, `PbrTaa`, `PbrSsao`, and others) are records
with init-only properties, so a focus pull, an exposure fade or fog that thickens must allocate a
copy every frame.

- Document the rule beside `PbrScene`: mutate lights and instances, and treat a settings record as
  a value that changes rarely.
- Fix the samples (`GiDemoScene.Animate`, the soft-shadow override loop and `RendererShowcaseScene`)
  to mutate their lights in place.
- Make the small settings `record struct`s. Assigning a copy then allocates nothing and change
  detection stays correct: `TemporalAntiAliasingFeature` compares the stored settings with
  `scene.Taa` and would miss an in-place mutation of a shared class, but a struct stores a copy.
  Making them mutable classes instead would break that check. This is an API change, best made in
  one release across every settings type. Audit each feature that caches a settings value first.

Validation: once the settings are structs, `--record-updates` mutates its lights in place and keeps
`with` for the settings, and `--workload showcase --record-updates --budget 0` passes.

### P1. Text input decodes through a string (SDL)

`SdlWindow.OnText` calls `Marshal.PtrToStringUTF8` for every text event. Decode runes directly
from `MemoryMarshal.CreateReadOnlySpanFromNullTerminated` with `Rune.DecodeFromUtf8`. This only
matters while typing; it is a small, local change.

## Guarding the result

1. Run the benchmark on the CPU lane: `--frames 600 --budget 0` for the reference and showcase
   workloads, and after S1 with `--churn 16`. Any per-frame allocation then fails CI and names its
   phase or feature.
2. Add `Paradise.Runtime.Test` coverage that runs the reference game for 120 frames on an
   allocation-free backend and asserts zero thread-local bytes per frame. The test's
   `RecordingBackend` copies every stream (`Commands.ToArray()`), so it cannot be that backend.
3. After W1 and W2, assert the WebGPU per-frame budget and the constant view-handle count on the GPU
   lane.

## Not yet measured

The benchmark covers the reference composition. These run during real games and still need to be
added: the presenting SDL and swapchain path (needs a display); ImGui and Noesis overlays;
behaviour-tree ticks, physics steps and `AnimationPlayer` with `JointPaletteStager` in the frame;
asset streaming (`Load` and `Release` while playing, whose per-load cost is expected but unbudgeted);
and Wwise audio. Each can become a workload switch and a phase in the same benchmark.

## Order

| Step | Change | Effect |
| --- | --- | --- |
| 1 | Guards 1 and 2 | Locks in today's zero for the engine frame |
| 2 | S1 | Spawn-heavy games stop allocating per spawn |
| 3 | W1 offscreen, W2 | WebGPU headless frames reach zero |
| 4 | W1 surface | Presenting frames stop leaving finalizable wrappers behind |
| 5 | A1 docs and samples, then settings structs | Animated settings and lights stop allocating |
| 6 | P1, then the unmeasured subsystems | Remaining sources |

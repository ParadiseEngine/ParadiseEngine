# Managed allocation during a game run

Steady-state frames allocate nothing: not the simulation, snapshots, asset loader, scene extraction or
renderer, not the WebGPU backend, and not a game that spawns and despawns renderables or animates lights
and scene settings. CI enforces it (`.github/scripts/allocation-budget.sh`).

Measured with [`Paradise.Runtime.Benchmarks`](../../src/Runtime/Paradise.Runtime.Benchmarks/README.md),
which drives the reference game's composition through the real hosts and attributes every byte to a
phase, a renderer stage or a built-in feature. Steady state means after the warm-up frames.

## Results

.NET 11.0.0, x64 Linux (WSL2), workstation concurrent GC; WebGPU on Dawn over the `dzn` driver.

| Scenario | Before | After |
| --- | ---: | ---: |
| Reference game (9 cubes, 1 light), null backend, cooperative or threaded host | 0 | **0** |
| Showcase (111 instances, 23 lights, GI, RTAO, SSR, fog, decals, full post: 41 passes), animating lights, DoF focus and exposure in place | 0* | **0** |
| WebGPU backend, reference or showcase | 128 | **0** |
| Churn: 16 of 256 entities replaced per tick | 2304 | **0** |
| Debug build (and Mono, which runs the browser backend), reference game | 64 | **0** |

Bytes per frame. \*Before, settings could not be animated in place; replacing them cost 312 B/frame.

Process-wide, across all threads, a measured window allocated 0 to 300 B in total however many frames
it spanned (240 B over 60,000 WebGPU frames); no window collected. Over the same 60,000 WebGPU frames,
live texture-view handles stayed at their starting count, where before they grew by one per frame until a
collection ran their finalizers.

## What changed

**WebGPU backbuffer.** The backbuffer view travels as a raw `TextureViewHandle` the presentation target owns
until `Present`. The offscreen target makes its view once with its texture. The surface target acquires
the swapchain texture and creates its view raw, then releases both after presenting. This also drops the
reference WebGPUSharp's `SurfaceTexture` marshalling added and never released. Render passes begin through
WebGPUSharp's raw descriptor (`CommandEncoderHandle.BeginRenderPass(in RenderPassDescriptorFFI)`), the one
its managed descriptor marshals to. Managed wrappers are made only for a capture or a native host pass on
the backbuffer.

**WebGPU frame pacing.** An offscreen target waits for the previous frame through
`WebGPU_FFI.QueueOnSubmittedWorkDone` with a static `UnmanagedCallersOnly` callback and
`InstanceWaitAny`, instead of `Queue.OnSubmittedWorkSync`, which allocates a closure and pins it per call.

**`PbrSceneBuilder`.** Slots are structs in the dictionary. A key that disappears, or changes mesh, retires its
instance at `End` to a free list for its mesh, and a new key with that mesh reuses it with every setting
reset. A retired instance always sits out a frame: the renderer keeps motion history only for instances it
drew in the previous frame, so the new key never inherits the old one's motion. Free lists idle for 120
frames are dropped, so pools do not keep released meshes alive.

**Scene settings are mutable.** The post, fog, AO, SSR, TAA/FXAA, motion-vector, instancing, contact-shadow
and probe GI settings take `set` instead of `init`, like `PbrLight`, `PbrAmbient`, `PbrTonemap` and
`PbrBloom` already did. Animate a setting by assigning its property; `with` still works, at the cost of a
copy. `TemporalAntiAliasingFeature`, the one feature that detected a settings change by comparing records,
compares a value snapshot instead, so an edit in place resets history as a replacement does.

**`HashedKey<T>.Equals`.** It compared through `EqualityComparer<T>.Default` instead of `_value is null`. The
null test boxed the query description on every ECS query wherever the JIT does not remove the box: Debug
builds, and Mono.

**SDL text input.** Text events decode runes from the native UTF-8 buffer instead of a string.

## Keeping it at zero

`allocation-budget.sh` runs on the Linux CI lane whenever the benchmark is built. It runs the reference,
showcase and churn workloads on the null backend, and the reference and showcase-with-churn workloads on
lavapipe, all at `--budget 0`. A failure prints the report, which names the phase or render feature that
allocated. `PbrCompositionTests.steady_spawn_churn_allocates_nothing` pins the builder on its own.

Game code keeps its frames at zero by mutating lights, instances and settings in place, and by keying
renderables by entity identity through `PbrSceneBuilder`. `--record-updates` measures the alternative:
replacing two lights and two settings with `with` costs 312 B per frame.

## Not yet measured

The benchmark covers the reference composition. These run during real games and are not in it yet: the
presenting SDL and swapchain path, which needs a display (its code is the surface target above); ImGui and
Noesis overlays; behaviour-tree ticks, physics steps and `AnimationPlayer` with `JointPaletteStager` in the
frame; asset streaming (`Load` and `Release` while playing, where a load is expected to allocate); and Wwise
audio. Each can become a workload switch and a phase in the same benchmark.

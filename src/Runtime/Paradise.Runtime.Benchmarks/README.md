# Paradise.Runtime.Benchmarks

Managed allocation during a game run. The benchmark runs the reference game's composition (ECS
simulation, snapshot publishing, `AssetLoader`, `PbrSceneBuilder` extraction, `PbrRenderer`)
through the real `CooperativeHost` or threaded `ParadiseHost` and reports, after a warm-up:

- process-wide bytes allocated per frame, collections per generation, GC pause time and working set;
- exact thread-local bytes per phase: simulation tick and publish, asset update, extraction,
  scene animation and `RenderFrame`, the last split into renderer stages and per built-in
  feature by probe features placed between them (`--no-probes` removes them);
- with the WebGPU backend, live WebGPUSharp native handles, which a finalizer releases only when a
  collection runs;
- with `--types`, the allocated types sampled from the runtime's `GCAllocationTick` events.

Results, the changes that reached zero and how CI keeps it there:
[docs/development/gc-allocation.md](../../../docs/development/gc-allocation.md).

```bash
dotnet run -c Release --project src/Runtime/Paradise.Runtime.Benchmarks
dotnet run -c Release --project src/Runtime/Paradise.Runtime.Benchmarks -- --workload showcase --entities 64
dotnet run -c Release --project src/Runtime/Paradise.Runtime.Benchmarks -- --backend webgpu --frames 3000
dotnet run -c Release --project src/Runtime/Paradise.Runtime.Benchmarks -- --entities 256 --churn 16 --types --no-probes --frames 20000
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--workload reference\|showcase` | `reference` | `reference`: the test's cube grid and one light. `showcase`: probe GI, ray-traced AO, SSAO, SSR, contact shadows, occlusion culling, fog with a volume, decals, 23 lights (3 shadowed) and the full post chain, modelled on the samples' renderer showcase |
| `--backend null\|webgpu` | `null` | `null` drops every upload and stream, isolating the engine; `webgpu` renders offscreen on Dawn |
| `--host cooperative\|threaded` | `cooperative` | `cooperative` runs exactly one simulation tick per frame (lockstep); `threaded` runs simulation at 60 Hz on its own thread |
| `--entities N` | 9 | Spinning ECS entities, each a renderable |
| `--churn N` | 0 | Entities despawned and replaced each tick, as projectiles or effects are |
| `--record-updates` | off | Showcase only: animate two lights, depth-of-field focus and exposure by replacing them with `with`, as the samples do, instead of assigning their properties in place |
| `--frames N`, `--warmup N` | 1200, 300 | Measured frames, and complete frames before measuring |
| `--width N`, `--height N` | 1280, 720 | Frame size |
| `--features list` | | Feature switch overrides, as `PARADISE_FEATURES` takes them |
| `--types`, `--top N` | off, 30 | Sample allocated types; needs enough frames to allocate several 100 KB |
| `--budget N` | | Exit with code 2 when any measured frame allocates more than N bytes (cooperative host; the threaded host checks the window's total against N × frames) |

Type sampling adds the event listener's own allocations to the process total, not to the phases.
The WebGPU backend needs an adapter; see the root README for Linux's lavapipe.

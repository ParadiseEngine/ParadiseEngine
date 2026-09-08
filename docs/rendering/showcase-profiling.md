# Renderer showcase profiling fixes

Follow-up to [issue #287](https://github.com/ParadiseEngine/ParadiseEngine/issues/287),
measured on 2026-09-08 against showcase commit `9c29bc7c886ef5f436010bdbf5632e37fd55d2e2`.

## Changes

- Timestamp readback maps once and polls its native future with zero timeout. It avoids the
  timed queue-wait/map-wait sequence that accumulated Windows handles during the device's lifetime.
  The synchronous API still returns the last submitted frame's timings. Mapping has a five-second
  deadline; cancellation has no managed callback state to outlive the call.
- Other synchronous readback map timeouts now use five seconds in native nanoseconds, rather
  than the previous five microseconds.
- The trace hierarchy compares copied model, mesh and material values before rebuilding and
  uploading. Membership changes, singular transforms and added geometry invalidate it. Material
  surfaces continue to refresh independently. Mutable instance references alone are not a cache key.
- The showcase caches feature labels and pass ownership, aggregates timings once per frame,
  and stops cloning lights whose soft-shadow setting already matches.
- A light already fully occluded by its shadow map skips contact-shadow ray marching. Quality
  settings and light counts are unchanged.

## Measurements

Windows 11 build 26200, .NET SDK 10.0.400/runtime 10.0.11, i5-12400, Radeon RX 6900 XT,
driver 32.0.21045.5002, WebGPUSharp 0.5.7. Release, 1280 x 800, `engine.toml`, deterministic
headless frames. The first 60 frames are excluded. Allocation and phase columns use the last
1,500 frames; iteration time averages the whole measured interval. GPU workloads ran sequentially.

| Measurement | Before | After |
| --- | ---: | ---: |
| Profiling enabled, 6,060-frame iteration mean | 3.637 ms | 3.392 ms |
| Profiling enabled, allocated bytes/frame | 66,768 | 9,329 |
| Profiling enabled, handles at frames 360 → 6,060 | 2,585 → 8,287 | 2,227 → 2,223 |
| Profiling disabled, 60,060-frame iteration mean | 2.404 ms | 2.298 ms |
| Profiling disabled, allocated bytes/frame | 32,180 | 8,470 |
| Profiling disabled, trace-build CPU phase | ~0.060 ms | 0.013 ms |
| Profiling disabled, handles at frames 360 → 60,060 | 2,227 → 2,224 | 2,226 → 2,216 |
| Profiling disabled, live managed heap after full GC | 6.51 MiB | 6.52 MiB |

An isolated 3,060-frame shader comparison with the CPU/readback fixes present in both runs gave
**1.917 ms → 1.829 ms** for the Main pass (4.6% lower). Whole-iteration means were 3.394 and
3.384 ms: that small difference does not establish a separate whole-frame shader speedup.
The scene region outside the timing panel (x >= 480, 640,000 pixels) matched the original and
both shader variants exactly at frame 3,060, with zero differing RGB pixels.

Polling has a profiling-only CPU cost: measured process CPU increased from approximately 0.32
to 0.98 logical cores in the timed soak. GPU timestamps remain hardware measurements, but
profiling-enabled CPU usage is not representative of normal rendering. With profiling disabled,
the corresponding process CPU measurements were approximately 0.45 and 0.43 logical cores.
The profiling-disabled soak kept GPU memory flat at 322.3 MiB dedicated and 53.9 MiB shared.
The installed wrapper's asynchronous map path was slow and still accumulated handles in a probe;
spontaneous callbacks without event pumping timed out. Neither was adopted.

These results do not establish a startup compilation improvement, a native Dawn fix, or a remedy
for speculative cache/lifecycle findings in the original report. Other synchronous tooling
readbacks still use timed native waits; the sustained handle regression here covers pass profiling.

## Reproduction and checks

Validation passed: 174 PBR tests and 67 WebGPU tests with profiling enabled (zero skips),
all three renderer Coyote tests at 200 schedules each, and the 160-frame showcase sweep
covering 29 switches, All off/Restore and resize. Release sample builds succeeded with
profiling enabled and disabled. No golden images were updated.

During validation, #286 merged as `f242f19` and its source branch was deleted. The fixes were
rebased onto that `main` commit. Its changes were limited to showcase input/fallback display
handling and removal of an unused SSR property. The five SSR tests, full showcase switch/resize
sweep and both sample build configurations were checked again after the rebase.

```powershell
dotnet run --project src/Paradise.Rendering.Sample -c Release -p:ParadiseProfiling=true -- --showcase --config engine.toml --bench --headless 6060
dotnet run --project src/Paradise.Rendering.Sample -c Release -p:ParadiseProfiling=true -- --showcase --config engine.toml --bench --headless 60060 --no-profile
dotnet run --project src/Paradise.Rendering.Sample -c Release -p:ParadiseProfiling=true -- --showcase --config engine.toml --headless 160 --sweep
dotnet test src/Paradise.Rendering.WebGPU.Test -c Release -p:ParadiseProfiling=true --output normal
dotnet test src/Paradise.Rendering.Pbr.Test -c Release -p:ParadiseProfiling=true --output normal
dotnet run --project src/Paradise.Rendering.WebGPU.CoyoteTest -c Release -p:ParadiseProfiling=true -- 200
```

The finer allocation/phase/handle samples used the temporary instrumentation published in #287;
that instrumentation is not included in the application changes. Local CSVs, logs and captures
are in `out/profiling-fixes`. Two repeat runs using a stale incremental-build backend after the
negative-control tests were excluded; fresh binaries were verified before the final comparisons.

The new native-handle test failed against the original implementation (+217 handles after its
2,000 measured clear frames) and passed with the fix. The hierarchy test likewise failed on the
original repeated upload and passed with caching. It checks transform edits, material changes,
new geometry, GI participation, singular transforms, removal and independent material refresh.

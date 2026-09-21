# Android rendering optimization: wake_up_room

## Result

The optimized NativeAOT Release player (version 0.6-specialized-rendering, code 6) is installed on the Xiaomi 2112123AC / Adreno 650 / Android 13 test device. Scene and UI remain at 2400 x 1080. Actual SurfaceFlinger presentation measured 60.6663 FPS across 1,274 fresh timestamps over 20.9836 seconds; median interval 16.4834 ms, p95 16.5538 ms, maximum 16.9478 ms. One scene load and the same process were observed, with no fatal startup/host failures.

This is NOT a verified on-screen 120-Hz result. Android keeps both primary and app-request display ranges at [0,60]. The engine's ANativeWindow_setFrameRate(120, DEFAULT) call returned 0, but the platform retained its 60-Hz mode. An attempted temporary peak-rate change was denied with WRITE_SETTINGS; the peak setting remained 60 throughout.

A separate on-device offscreen benchmark, with Noesis UI enabled, completed 960 GPU-fenced frames in 7.930408 seconds (121.053 frames/second). It allowed at most three submitted frames between completion fences, so this is completed GPU work, not an unbounded CPU submission rate. Story, movement and shader time were frozen for that benchmark. The final screenshot overhead was included. Its short duration and tiny margin above 120 do not establish sustained or presented 120 FPS.

A serial diagnostic measured approximately 7.057 ms Main GPU time and 7.621 ms summed timed passes, plus an 8.707 ms synchronized render callback with UI. The native UI host pass is not individually GPU-timestamped; it participates in the completion fence and callback wall time. These serial timings came from the v4 experiment; final v6 retains those shader changes and leaves the unrelated sorting experiment disabled.

## Implemented optimizations

1. Pipeline override constants are now supported by the shared rendering contract, native WebGPU vertex/fragment/compute stages, and browser WebGPU JSON/JavaScript stages. Pipeline cache equality, hashes and owned key snapshots include constant values. Slang specialization parameters are not mistaken for descriptor-table resources.
2. PBR specialization uses the frame-frozen feature switches to remove disabled shadow, GI, AO, reflection and decal shading paths. This is not merely omission of the producer passes. Older custom shaders without the override retain their existing path and layout.
3. Immutable material intent removes procedural recipes and texture fetches that cannot contribute. Missing maps use the existing fallback texel values, including the exact 128/255 normal-map fallback, rather than changing the material appearance. Stock, custom, skinned and instanced paths have distinct correctly keyed variants.
4. Fully hidden and fully intact roof draws exit before unnecessary dissolve noise. Partial dissolves and the separate depth/shadow program remain unchanged. A native GPU regression compared the original and optimized shader at progress 0, 0.25, 0.5, 0.75 and 1 with exact pixels and confirmed the shadow pass remains present.
5. The Android configuration requests a preferred 120-Hz presentation rate. This is a hint, not a simulation tick change or a bypass of system display policy. API 26 remains supported via runtime resolution of the API-30 native export.

No resolution reduction, unlit replacement, geometry removal, authored asset rewrite, renderer migration, or change to NativeAOT was used. The prior minimal Android feature configuration remains in place. Audio remains unavailable as before.

## Configuration

Source: ShiningPie.Android/engine.conf, packaged at /android/engine.conf, existing TOML syntax:

```toml
[[features]]
name = "rendering.presentation"
enabled = true
preferredFrameRate = 120.0
```

The optional near-to-far opaque ordering experiment did not improve this tiled GPU materially and is disabled (sortOpaqueFrontToBack = false). A single-light loop specialization regressed performance and was removed. Neither is counted as a successful optimization.

## Validation

- Rendering contract suite: 161 passed.
- Native WebGPU suite: 89 passed, one profiling-only test skipped.
- PBR specialization: 5 passed; optional ordering: 2 passed; native GPU feature toggles: 9 passed.
- Browser managed contracts: 14 passed; browser JavaScript lifetime/stage-constant tests: 10 passed.
- Android host: 18 passed; Android configuration: 8 passed.
- Packaging and real Java content installer: 47 passed.
- Roof endpoint/partial-dissolve GPU image regression: 1 passed.
- Release native compilation, ARM64/16-KB alignment, signatures and final content/native inventory passed. This phone uses 4-KB pages, not a 16-KB runtime qualification.
- git diff --check passed for engine and normal-player trees.

These are targeted suites, not a rerun of all PBR/game tests. Physical browser GPU, iOS, long-duration thermal behavior, native-surface replacement and the 120-Hz on-screen acceptance test remain open.

## Reproduction and source state

Engine: C:/proj/mcp/ParadiseEngine-android-120hz (perf/android-120hz).
Normal game: C:/proj/mcp/ShiningPie-android-120hz-player (perf/android-120hz-player).
Diagnostic game: C:/proj/mcp/ShiningPie-android-120hz (perf/android-120hz).
Local coherent engine package feed version: 0.50.2-android.hz120.6. These packages are not published; normal CI/release adoption requires publication and pin updates. No engine ProjectReference substitution was added.

APK: C:/proj/mcp/gen/shiningpie-android/120hz/player-artifacts/ShiningPie-NativeAOT-Release.apk
Bytes: 98,923,594
SHA-256: cafb2baac193aca69d2e30baf2968572802f95b95284f1a8969239e09a8dec2a
Development-key signed. App data preserved. Debug was not rebuilt for these optimizations.

Source remains uncommitted and unpushed; existing draft PRs were not updated. The normal game contains no frozen-story/profiling mode. Fullscreen/no-title behavior remains.

## Evidence files

All paths below are relative to C:/proj/mcp/gen/shiningpie-android/120hz:

- verified-final-results.json: selected results and caveats.
- display-current-policy-validation.json: raw fresh display timestamps, display policy, thermal state and game logs.
- display-current-policy-game.png: on-screen game capture.
- display-verification-run.log: denied system-setting write; no changed cap.
- device-room-throughput-ui.log: every completed three-frame batch and final throughput summary.
- device-headless-room-v4-ui.log: serial GPU timing observations.
- player-artifacts/ShiningPie-NativeAOT-Release-validation.json: signed package validation.
- *final-tests.log, player-settings-tests.log, player-packaging-tests.log, roof-endpoint-tests-v6.log: test evidence.

Android's refresh-rate request is intentionally non-binding: https://developer.android.com/media/optimize/performance/frame-rate
WGSL pipeline override semantics: https://www.w3.org/TR/WGSL/#override-decls

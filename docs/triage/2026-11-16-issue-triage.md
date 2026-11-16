# Issue triage — 2026-11-16

Scope: every open issue in `ParadiseEngine/ParadiseEngine` (50) and `ParadiseEngine/ParadiseSamples` (0).
Baseline: `main` at `f74be8a` (the #367 merge). Open PRs: #326 (Android, draft), #370 and #371 (SDF renderer; no tracking issue).

Each status below was checked against code on `main`, not just against the issue text. Several issues were
filed before #350 (2026-10-16) and #367 (2026-11-14) merged, and are now partly or fully stale.

## Scales

**Priority**

| | Meaning |
|---|---|
| **P0** | Silent data corruption or a broken release. Do now. |
| **P1** | A correctness bug on an ordinary path, or work blocking a downstream consumer. Do this cycle. |
| **P2** | Important. Scheduled after the P1s in its wave. |
| **P3** | Improvement or hygiene. Opportunistic. |
| **P4** | Parked: a decision record, research, or an issue that says "not yet". |

**Category** uses the existing labels where one fits: `ecs`, `rendering`, `assets`, `editor`, `gui`, `ci`, `aot`,
`documentation`. Areas without a label are marked *(no label)*: platform, animation, physics, authoring, bt, jobs, research.

**Type**: bug · feature · test-debt · perf · umbrella · research · record.

---

## Wave 0: housekeeping (about a day, no engineering)

Close or narrow issues whose work has already landed, so the waves below count only real work.

| # | Title (short) | Category | Action | Evidence |
|---|---|---|---|---|
| #366 | Architecture review at v0.62.0 | umbrella | **Close** | #367 delivered findings 1–7 and 9–13. Finding 8 continues in #369, the Noesis failures in #368. |
| #49 | Reword JobWorkerPool latch comment | ecs / jobs, documentation | **Close** | The latch is gone: no `latch` remains in `src/Foundation/Paradise.Jobs` after #350 and #367. |
| #162 | No engine-side asset layer | assets | **Close; open one narrow follow-up** | `Paradise.Runtime.AssetLoader` (ref-counted handles, worker decode, bounded upload, unload) came in #367, Zio mounts in #230, packs in #360. Still missing: an HTTP/fetch source for browser hosts, and moving downstream hosts onto the loader. |
| #178 | ECS.Managed design plan | ecs | **Close in the engine** | Phases 0–3 shipped in #317. Phase 4 (consumer migration) belongs to the game repos. |
| #333 | ImGui vs Prowl.Paper benchmark | editor, aot | **Close as decided** | The conclusion is to stay on Hexa ImGui. Keep the issue as the record. |
| #231 | Zio vs System.IO rationale | documentation | **Move to `docs/development/` and close** | The issue says it proposes no change; it is a decision record. |
| #298 | Publish Features packages / dependency closure | ci | **Narrow** to the closure half | #367 derives the pack list from `IsPackable` and gates publishing on tests. Staged-package consumer smoke and dependency-closure checks are still missing (Wave 1). |
| #299 | Invalidate outputs when importer code changes | assets | **Narrow** | #348 put each importer's type and `ModuleVersionId` into the build environment key. Still missing: a regression test (two importers, same source, different bytes) and a signature for importer-specific tools and configuration (Wave 1). |
| #287 | Showcase native handle / profiling report | rendering | **Update body** | #288 merged on 2026-09-08. The body still calls it an unmerged draft. Keep only the remaining investigations (Wave 5). |
| #349 | Implementation & architecture review roadmap | umbrella | **Tick the delivered boxes** | #350 delivered the Stage 1 batch. #367 delivered the Stage 3 CI, GPU-lane, Coyote and host-driver items and the asset-loader item. Keep the issue as the umbrella. |
| #228 | Browser ImGui spike | gui, aot | **Update body** | #367 added `SetScissorRect`, `WriteTextureRegion` and an `ImGuiRenderer` over `IRenderer`. #333 proved the NativeAOT-LLVM route. What is left is the cimgui wasm build and a browser host (Wave 4). |
| #222, #224–#227 | Editor E1–E4 | editor | **Re-scope** | #304 removed the standalone `Paradise.Editor.Host` and its smoke CI; the editor now embeds in the game's overlay. E1's dock/workspace design and E4's child-process play mode assume the old host (Wave 6). |
| #365 | TLA+ for concurrency designs | research | **Update paths and order** | The job pools moved to `src/Foundation/Paradise.Jobs`. `WorkStealingPool` is now `[Experimental]` and `JobWorkerPool` is the supported pool, so the first spec should model `JobWorkerPool`. |

Unlabelled issues that need labels: #372, #369, #368, #366, #365, #362, #356, #355, #349, #344, #291, #287, #184, #183, #164, #153.

---

## Wave 1: correctness and release safety (P0/P1 bugs)

These now matter more: since #367, CI runs the GPU, Coyote and macOS/Windows lanes, so fixes here can be verified.
Most are also acceptance gates of #349.

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| **P0** | #356 | `assets extract` binds GLB materials by index | assets | bug | It silently rewrites committed `.material` documents and duplicates a guid. Seen in ShiningPie (0.59.1). Still on `main`: `AssetExtractor.Materials` pairs by `m.Index == i` (line 800). Clips already pair by name first (lines 588–612); reuse that for materials. |
| **P1** | #355 | Material `DoubleSided` never consumed; no cull mode | rendering, assets | bug | Seen in ShiningPie: backfaces always render. `Export.Contract` has no `DoubleSided`, and `Paradise.Rendering` has no `CullMode`. Touches the contract, the PBR material desc, pipelines and the browser shim. |
| **P1** | #294 | Validate mesh blob offsets before native dereference | assets | bug | `MeshBlobFormat.Open` still checks semantics only after following relative pointers. It is #349 Stage 1; extend the same check to animation blobs. |
| **P1** | #297 | Material resource creation/replacement exception safety | rendering | bug | `MaterialResourceCache`: leaks on partial failure, and the old bind group is destroyed before its replacement succeeds. Test with a failure-injecting `IRenderer`; no GPU needed. |
| **P1** | #296 | Coherent feature configuration snapshot | ecs | bug | `TakeFeatureSnapshot` still reads `IsEnabled` per system. `RenderPipeline.BeginFrame` has the same shape. Fix it at the `Features` boundary and add Coyote coverage. |
| **P1** | #300 | Browser renderer: isolate sessions or enforce one | rendering | bug | `paradise-webgpu.js` still holds one global state object `G`, and the managed side has no guard against a second session. A guarded single-session API is the cheapest correct fix. |
| **P1** | #368 | Ui.Noesis: five GPU tests fail on every CI adapter | gui, ci | bug | Quarantined with `KnownFailure.SkipInCi`, and the skip budget counts them. Fails the same way on lavapipe, WARP and Metal, so it is product or test behaviour, not a driver. |
| **P1** | #298 *(narrowed)* | Staged-package dependency closure | ci | bug | Restore and build a consumer against the exact staged packages before pushing to NuGet. #349 Stage 3 asks for the same. |
| P2 | #299 *(narrowed)* | Importer identity: test and tool/config signature | assets | test-debt | The key already covers the importer's code. Add the regression test and the declared signature for external tools. |

Remaining #349 Stage 1 items without their own issue: `RepeatTimes`/`RepeatForever` subtree reset, `ManagedBlobAssetReference<T>` size check, repeatable `AnyTreeBuilder` (if #350 did not cover them). Verify, then split out or tick.

---

## Wave 2: consumer-driven features (ShiningPie and games)

Runs alongside Wave 1; different subsystems.

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| **P1** | #354 | `assets convert --deform-bones` for `.blend` rigs | assets | feature | Will be ShiningPie's default (ADO #18). Small: thread `export_def_bones` through `Verbs.Convert`, `BlenderModelConverter` and the script, and add it to the stamp/cache key. |
| **P1** | #316 | Per-clip root motion with root snap | assets, animation | feature | Spans the engine, the Blender addon (#47) and ShiningPie (ADO #14). Builds on #347's `AnimationPlayer`. |
| P2 | #358 | Character ground support: grounded, ledge, snap | physics *(no label)* | feature | All the query vocabulary exists (`CastRay`/`CastCollider`, layer masks). Mostly API plus a walkable-layer contract. |
| P2 | #247 | Avatar: one skeleton shared by meshes and clips | animation, assets | feature | Needed for clip packs. #164 section A points here. |
| P2 | #362 | Authoring: `HostSpline` kind (Bezier knots and handles) | authoring *(no label)* | feature | Needs matching writers in the Blender and Godot hosts. |
| P2 | #268 | Format-neutral generated `[Authored]` readers | authoring *(no label)* | feature | Unblocks TOML-profile documents and drops per-game `TomlSerializerContext`s. Engine-only; the baked contract stays JSON. |
| P2 | #359 | Runtime debug console (REPL, debug views, cheats) | gui, tooling | feature | Can render through the new `ImGuiRenderer`, so it works in the browser too. |

---

## Wave 3: concurrency qualification and test debt

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| P2 | #349 Stage 2 | Concurrent ECS races, handle generations, native lifetimes | ecs | umbrella | `ECS.Concurrent` is now marked experimental (PECS9000). Either qualify it or keep it experimental, but decide. ECB placeholder generations and world `Clear` generations affect the core world. |
| P2 | #50 | ECB coverage under both wave schedulers | ecs | test-debt | Still missing: `Paradise.ECS.Jobs.Test` has no command-buffer tests. |
| P3 | #48 | `_cachedAdapter` thrash when `T` alternates | ecs / jobs | perf | Still present in `Paradise.Jobs` (`JobWorkerPool.cs:52`, `WorkStealingPool.cs:41`). Document single-`T` use or key the cache by type. |
| P3 | #291 | SnapshotStream queue contention | ecs / hosting | perf | Measure first; keeping the current design is a valid outcome. |
| P3 | #365 | TLA+ specs, starting with the job pool | research | research | After Wave 0's update. Complements, not replaces, the Coyote lanes. |

---

## Wave 4: platform reach (mobile, browser, runtime)

#323 is the owner's active direction and is already in flight (draft #326), so it runs in parallel and is not held for earlier waves.

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| **P1** | #323 | Android: native hosting, WebGPU/Vulkan, release | ci, aot, assets, rendering | umbrella | NativeAOT/Bionic bootstrap exists locally. Next gate: a physical device. `CooperativeHost` and `HostClock` from #367 are the lifecycle base. |
| P2 | #324 | iOS: native hosting, WebGPU/Metal, release | ci, aot, assets, rendering | umbrella | Starts after Android's shared lifecycle contract settles. Do not start in parallel (the issue says so). |
| P2 | #344 | Migrate to .NET 11 | ci, aot | feature | Wait for .NET 11 GA. Coordinate with #323, which pins runtime 10.0.12 for Bionic. BenchmarkDotNet 0.15.8 is known to break on .NET 11. |
| P2 | #228 *(updated)* | Browser ImGui: cimgui wasm build and browser host | gui, aot | feature | The renderer half is done. Also unblocks UI for #359 and the editor in a tab. |

---

## Wave 5: rendering scale and performance (measure first)

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| P2 | #369 | Lights and shadow views in storage buffers; quantised vertices | rendering | perf | The rest of #366 finding 8. `FrameUniformsGpu` is 49.6 KB of a 64 KB limit, so headroom is shrinking. Do item 3 together with #315. |
| P2 | #302 | Silhouette outline rendering | rendering | feature | Needed for editor selection and gameplay highlights. The issue recommends approach B (R8 mask plus dilation). |
| P2 | #315 | meshoptimizer in the cooker, staged runtime support | assets, rendering | perf | Pair with #369 item 3; it also feeds #309's proxy geometry. |
| P3 | #309 | DDGI for large scenes (cascades, streaming) | rendering | feature | Partly done in #311. |
| P3 | #313 | Fog cost: analytic unlit fog, reduced-resolution volumetrics | rendering | perf | |
| P3 | #249 | Baked pose textures for crowds | animation, rendering | feature | Independent of #247. |
| P3 | #287 *(updated)* | Native readback failure paths; startup attribution | rendering | perf | Only investigations remain. |
| P4 | #372 | Hardware ray tracing research | research | research | Recommendation: keep the compute tracer and revisit when Dawn implements the gpuweb ray-query proposals. |

---

## Wave 6: editor and UI tooling (after re-scope)

| Pri | # | Title (short) | Category | Type | Notes |
|---|---|---|---|---|---|
| P3 | #222 | Paradise.Editor parent | editor | umbrella | Rewrite the architecture for the embedded-overlay model (#304), then re-cut E1–E4. |
| P3 | #224 | E1: shell (registries, dock layout, palette) | editor | feature | Keep the `EditorDockspace` uniqueness guard from the 2026-09-03 comment. |
| P3 | #225 | E2: six panels on mock data | editor | feature | |
| P3 | #226 | E3: read-only real data from `assets/` | editor | feature | |
| P3 | #227 | E4: play-in-editor | editor | feature | Its child-process design assumed a standalone host. Most affected by #304. |
| P2 | #235 | Font passes the sniff but aborts inside stb_truetype | gui | bug | A native abort ends the process, and system fonts are untrusted input. Retype as a bug. |
| P3 | #234 | Pick a UI font by its cmap, not a name list | gui | feature | Pairs with #235: parsing cmap also validates the tables. |

---

## Backlog and parked (P4)

| # | Title (short) | Category | Why parked |
|---|---|---|---|
| #164 | Advanced skeleton animation | animation | Umbrella. Its scheduled parts are #316, #247 and #249. State machine, locomotion and motion matching wait for a game need. |
| #183 | Generated blackboard for external BT blobs | bt | No external BT authoring tool consumes it yet. |
| #184 | BT instance pool for large trees | bt | The issue says "not yet": the shipped tree has nine nodes. |
| #153 | Fold the Godot and Blender editors into this repo | ci | Deferred by decision on 2026-08-21. Its "worth doing anyway" items (Blender CI, contract-check) belong in that repo. |

---

## Cross-cutting notes

- **Overlaps.** #349 is the umbrella for #294, #296, #297, #298, #299 and #300; those issues stay the acceptance gates. #369 item 3 and #315 are one change. #355 and #356 both came from ShiningPie on 0.59.1.
- **Untracked PRs.** #370 and #371 (SDF renderer, GI, effects) are open and ready but have no issue. Add a tracking issue if the SDF path will be maintained.
- **Stale paths.** Issues filed before #328 (2026-09-24) cite pre-reorganisation paths such as `src/Paradise.ECS.Jobs/...`. They now live under `src/<Subsystem>/...`, and the job pools under `src/Foundation/Paradise.Jobs`.

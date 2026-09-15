# Neon-city renderer optimization measurements

Measured on 2026-09-16 using ShiningPie's neon-city scene. Disabling the authored fog
reduced median elapsed renderer-call time from 16.4922 ms to 6.9030 ms. The candidate
renderer reduced it further to 6.0256 ms, with indexed draw calls falling from 7,743
to 980. These are frozen-scene measurements, not live gameplay frame rates.

## Method

Apple M3 Max, .NET 10.0.11, Release, `ParadiseProfiling=true`, WebGPU, headless
1280 x 720. `DOTNET_TieredCompilation=0` and
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` were set for every run. Each main variant
ran in three fresh processes with 120 warmup frames and 600 measured frames: 720
submitted renderer frames per process. One additional unbatched control used the
same warmup and sample counts.

The harness uses the actual ShiningPie scene constructor and assets. It freezes the
starting camera, lights, transforms, time, instance flags and GI settings while the
host and simulation continue running. UI, overlays, audio, story start and movement
are disabled. The scene contains 2,298 mesh instances, 4,234 primitive instances and
25 lights. GI uses a 19 x 8 x 12 volume (1,824 probes), 128 rays per probe and 256
probes updated per frame. Camera-frustum culling is enabled; GPU occlusion is
disabled. Other authored effects remain enabled as configured.

The variants are:

- **Old + fog:** original renderer and authored fog; orthographic clustered lighting
  disabled by the old game workaround.
- **Old, fog off:** the same original renderer with fog disabled.
- **Optimized, fog off:** candidate renderer with custom-material bounds culling,
  custom-material instancing, opaque regrouping, batched depth and shadow passes,
  light-frustum shadow culling, packed shadow uploads and corrected orthographic
  clustered lighting. The game's custom materials opt into the applicable bounds
  and instancing contracts.
- **Optimized unbatched control:** candidate with `rendering.instancing=false`,
  disabling main, depth and shadow batching and effective opaque regrouping. Bounds
  culling, light-frustum shadow culling and orthographic clustered lighting remain.

The field named `medianCpuMs` in the raw results measures elapsed renderer-call
duration, including native submission and driver waits. It is neither CPU
utilization nor pure CPU execution time nor GPU-only time. The primary runs do not
synchronize the GPU after every frame. Pre-submit time reports the instrumented
portion before submission and helps expose the cost of preparing batches.

Comparability validation checks completion, frame counts, binary identities,
stable in-run scene fingerprints, the shared frozen snapshot, camera, dimensions,
GI, scene settings and feature switches. It allows only the intended fog,
light-culling, instancing and reorder differences. All ten completed runs pass.

## Timing

For the three main variants, values are medians of the three per-run statistics.
The range is the minimum and maximum of the three renderer-call medians. The p95
column is the median of the three per-run p95s, not a percentile pooled across runs.

| Variant | Renderer call median | Range of run medians | Pre-submit median | Renderer call p95 |
| --- | ---: | ---: | ---: | ---: |
| Old + fog | 16.4922 ms | 16.4400-17.1679 ms | 1.3436 ms | 17.6920 ms |
| Old, fog off | 6.9030 ms | 6.8188-6.9474 ms | 1.1506 ms | 8.0247 ms |
| Optimized, fog off | 6.0256 ms | 6.0067-6.1606 ms | 1.5875 ms | 6.7541 ms |
| Optimized unbatched control | 6.0672 ms | One run | 1.2585 ms | 7.0177 ms |

Disabling fog accounts for a 58.1% reduction from the original scene. The candidate
adds a 12.7% reduction against the fog-disabled baseline (0.8774 ms), for 63.5% less
renderer-call time overall.

Batch preparation has a measurable cost: pre-submit time increases by 0.4369 ms
against the fog-disabled baseline, from 1.1506 ms to 1.5875 ms. The unbatched
candidate takes 6.0672 ms overall, only 0.0416 ms more than the batched candidate and
within the variation of the three main candidate runs. This single control does
not establish a substantial total-time benefit from batching alone. Culling and
clustered lighting together account for most of the observed improvement beyond
fog, but the control does not isolate their individual contributions.

## Draws and uploads

| Indexed draw calls per frame | Old, either fog state | Optimized | Optimized unbatched |
| --- | ---: | ---: | ---: |
| Main | 1,724 | 214 | 1,138 |
| Depth/normal prepass | 1,785 | 191 | 1,138 |
| Shadows | 4,234 | 575 | 2,231 |
| Total | 7,743 | 980 | 4,507 |

The candidate submits 87.3% fewer indexed draws. Camera-frustum culling removes
3,096 primitive instances instead of 2,449, and light-frustum culling removes
2,003 shadow primitive instances. After culling, batching saves another 924 main,
947 prepass and 1,656 shadow draws. Indexed primitive instances across these passes
fall from 7,804 to 4,507; batching reduces calls without further reducing that
instance count. Recorded command counts fall from 33,431 for the old fog-disabled
renderer to 4,832 with batching (19,889 for the unbatched control).

| Measured mean per frame | Old + fog | Old, fog off | Optimized | Optimized unbatched |
| --- | ---: | ---: | ---: | ---: |
| Uploaded bytes | 3,122,462.53 | 3,122,398.53 | 2,454,690.53 | 1,729,970.53 |
| Managed allocated bytes | 9,736 | 9,648 | 9,648 | 9,648 |

Upload values average `metrics[].Uploads.UploadBytes` over the 600 measured frames;
the warmup `drawCounts.UploadBytes` snapshot is not the measured mean. These values
are identical across repeats within each variant. Joint uploads are zero in this
frozen scene, so these runs do not establish an animation or skeleton speedup.

Candidate uploads are 21.4% lower than the fog-disabled baseline. The unbatched
control uploads still less because the main uniform ring remains allocated and
uploaded while batching adds storage streams. Packed shadow records reduce total
bandwidth, but enabling batching does not itself reduce every upload category.
Reducing duplicate main-uniform uploads and batch preparation is a useful next
target; a lower draw count alone does not guarantee lower elapsed time here.

## Image comparison

Final images are byte-identical across the three repeats of each main variant.
The old fog-disabled image and the optimized unbatched image are byte-identical.
The fully optimized image differs from the old fog-disabled image in 99 of 921,600
pixels (0.01074%), covering 148 channel bytes. Alpha is unchanged. The maximum
channel difference is 8/255; 88 of the 99 affected pixels differ by at most 1/255,
11 exceed 1/255, and only three exceed 2/255. RGB mean absolute error is
0.00006547 and RGB RMSE is 0.01094163 in 8-bit channel units.

The sparse edge-localized differences are confined by the control to the
batching/regrouping path. The comparison does not distinguish numerical rounding
from changes to equal-depth winners. Opaque reordering explicitly permits the
latter. Enabled batching therefore does not claim exact pixel equivalence. The
fog-enabled image intentionally differs in appearance.

SHA-256 of the final raw BGRA pixels:

| Variant | SHA-256 |
| --- | --- |
| Old + fog | `5CD2D29C958DD12AE41ED6503C9AEF4DDAB07AE73E10877317EC2DBE12ABD366` |
| Old, fog off / optimized unbatched | `6E90A5B8F2C500E19611A71BF522C247AE232B6F6B83233B52E0B15C588EB893` |
| Optimized | `25F6D61D990E6E1D3B4D8868EA5E3644251EF1E086CF39F02AFA743F34FB0A08` |

## Provenance and reproduction

All timing runs use source-built JIT binaries. The baseline engine is
`c772fb3e4ea996097c8280c952c460ed86cf3b00`; the baseline game is
`2220a2845ef5e1520d82cbed760d2d9f04286af9`. The candidate was measured from working
engine changes on `33f2722bbaa7c18a316a460c46fd5856a83a8b3a` on the PR #321 branch
and working game changes on the same game commit. The candidate measurements must
not be attributed to those base commits alone. The recorded candidate tracked
game diff has SHA-256
`bac8c1f607484f82b085c2e9c1b4c130b204f7b6f68b323abf9f8a6513e3ea41`.
The game changes were subsequently committed as
`5ded8bb0f416b1e97637ffa1266671141070cb3a`, including authoring-default synchronization
and dependency documentation added after measurement without further rendering
changes.

| Binary identity | Baseline | Candidate |
| --- | --- | --- |
| `Paradise.Rendering.Pbr.dll` SHA-256 | `938E920888001A4508EE05CC5706E78DD31A6647E6A264775941789AC7968B0B` | `811F53BAAA21FDF9DA5C9F93100E5C45739A8150C99DED22496B9FFD9F7FDE40` |
| Reflected shader resources | 26 | 28 |

The shared frozen snapshot has SHA-256
`fd65e471fcff723b30761e72625e2b4e53ef9832c42cd0057b926e1d6d7a0ec9`.
The original authored scene TOML hash is
`43cbafc95c0fed3a06e44c9c69fb3ac1b4ba86381133ecdad93d584e02da1b38`;
the updated no-fog scene hash is
`a9665d747326692373f1adc39d58169319517e03d270f0ee2329a271691c0131`.
The scene changes set an empty `SceneStory`, `ReorderOpaqueDraws=true` and
`FogEnabled=false`; the harness also explicitly controls story start and reordering
per variant. Runtime validation checks geometry against the shared snapshot.

Temporary raw artifacts are under `/tmp/paradise-neon-optimized-9vfxgfpi`:

- `results/{old-fog,old-no-fog,optimized}-{1,2,3}/` and
  `results/optimized-unbatched-control/`: exact command, log, results JSON, frozen
  snapshot and fingerprint, raw BGRA pixels and PNG.
- `source-frozen-scene.json`, `run.py`, `check_comparability.py` and
  `comparability.json`: frozen inputs, runner and cross-run validation.
- `candidate-bin/`: candidate binaries. Baseline binaries are under
  `/tmp/paradise-frame-packing-klBMqM/neon-baseline-bin/`.

While these temporary artifacts remain available, the following commands reproduce
the candidate with a fresh output label and validate all ten recorded runs:

```sh
python3 /tmp/paradise-neon-optimized-9vfxgfpi/run.py optimized --repeat reproduction
python3 /tmp/paradise-neon-optimized-9vfxgfpi/check_comparability.py \
  /tmp/paradise-neon-optimized-9vfxgfpi/results/{old-fog,old-no-fog,optimized}-{1,2,3} \
  /tmp/paradise-neon-optimized-9vfxgfpi/results/optimized-unbatched-control
```

The runner requires a new result directory and rejects missing or incomplete
results. The instrumentation and frozen-scene harness are temporary benchmark
tools, not normal launcher behavior; an ordinary scene launch does not reproduce
these frozen inputs.

A local `Paradise.Rendering.Pbr.0.48.0.nupkg` candidate was produced under
`packages/` for separate package and NativeAOT validation. Its version label does
not establish that the same contents are available from released NuGet packages.
These timing results describe the identified source-built JIT binaries and do not
measure package consumers or NativeAOT performance.

Separate candidate validation passed NativeAOT publication and 120-frame runs of
neon city, the overview and the animated district, each with UI enabled. This
checks the candidate's build and execution path, not its published-package status
or NativeAOT timing relative to the baseline.

# Asset pack validation — 2026-10-16

Environment: Windows x64, Intel Core i5-12400 (6 cores / 12 logical processors), .NET SDK
10.0.402, desktop runtime / NativeAOT 10.0.13, browser workload 10.0.11, Edge 155.0.4283.45.
Codec: ZstdSharp.Port 0.8.8, level 3, 256 KiB chunks, SHA-256 verification enabled.

## Prefab-root selection

An isolated copy of ENCORE's `assets/` and converted model cache was cooked with JSON documents,
full texture quality, and these roots. The original project and its build outputs were unchanged.

```toml
[build.profiles.pack_measurement]
pack = true
document_format = "json"
texture_quality = "full"
pack_roots = ["levels/basement.prefab", "catalogue/items.prefab"]
```

Both comparison packs use the same freshly cooked tree and codec settings, including their
respective full or filtered manifests and all container overhead.

| Selection | Runtime assets | Decoded bytes including manifest | Complete pack bytes |
| --- | ---: | ---: | ---: |
| All cooked outputs | 2,658 | 60,386,322 | 10,669,129 |
| Declared roots and runtime dependencies | 11 | 7,662,671 | 1,258,326 |

Selection removes **87.311%** of decoded bytes; compression then saves **83.578%** of the selected
bytes. The resulting pack is **88.206% smaller** than the all-assets pack. This JSON recook is a
different corpus from the earlier existing-build measurements below. No loose Wwise files occur
in this comparison. All selected files matched their manifest sizes and SHA-256 after decoding.

This measures the declared roots, not a complete playable ENCORE release. Its renderer also
derives animation clip paths in code (`Rig.Load`), which need explicit extra roots; separately
shipped fonts and audio remain outside these totals. Other levels were deliberately not selected.
Arbitrary game-data strings are not inferred as references. The experiment demonstrates removal
of unused cooked output, not that these two roots describe every file the host can load.

Selected pack SHA-256:
`9e04a54d0c9124fedfbb2d747178e50702b8e2b6922ddb4df6a797d117db71aa`.
The isolated snapshot, complete comparison pack, selected pack and report are under
`.scratch/pack-roots-encore`; the local measurement driver is `.scratch/pack-roots-measure`.

Local checks after adding selection: **633 pipeline tests passed / 8 existing platform or
missing-tool skips; 125 project-manifest tests passed; 41 packing tests passed**. Coverage includes
nested instances versus runtime prefab references, material-to-texture and skinned-mesh-to-skeleton
dependencies, unused sibling animation exclusion, dynamic roots, GUID roots across renames,
deduplication and cycles, filtered identities, empty/invalid roots, persisted and warm reuse,
unselected edits avoiding pack publication, missing dependencies preserving the previous pack,
event-free recovery, root/profile changes, and switching back to all-assets or loose builds.
The pack reader and format are unchanged; the NativeAOT/WASM results below still apply to that code.

## Real project compression

Measured the existing `C:\proj\mcp\BackpackCult\build` (ENCORE) and
`C:\proj\mcp\shiningpie\build` trees with the new `AssetPackWriter`, without modifying either
project. The input is exactly each build manifest's files plus that manifest. Wwise `.bnk` /
`.wem` files remain loose. All sizes below include the container's TOC, paths, hashes, header
and footer; MiB means 1,048,576 bytes. Every source matched its manifest length and SHA-256,
and every packed file matched again after decompression at all three measured chunk sizes.

| Corpus at the 256 KiB default | Packed files | Before | After | Reduction |
| --- | ---: | ---: | ---: | ---: |
| ENCORE cooked manifest | 2,659 | 55.291 MiB (57,976,989 B) | 10.076 MiB (10,565,005 B) | **81.777%** |
| Shining Pie packable files | 90 | 4.579 MiB (4,801,172 B) | 1.339 MiB (1,403,933 B) | **70.759%** |
| Shining Pie including loose Wwise files | 90 packed + 61 loose | 63.247 MiB (66,319,726 B) | 60.008 MiB (62,922,487 B) | **5.123%** |

ENCORE's manifest contains 2,658 assets and is 1,638,731 bytes itself. Its recorded profile is
empty (the default), with a build timestamp of 2026-10-16 06:57 UTC. This is actual cooked
geometry, animation and document content, not generated benchmark documents. Its checkout HEAD
was `e8efe49ff61792ad1f3c9e0e773178492e9e0f34` with local changes; the manifest hash below
identifies the measured build, rather than claiming it matches that commit. Shining Pie's
150-asset manifest is an older `dev` build dated 2026-09-01 07:52 UTC, with 61,518,554 bytes of
Wwise banks/media excluded from compression. Neither corpus was recooked as a release profile.

**Scope:** these are asset-build sizes, not whole-game downloads. ENCORE copies fonts, music
scores and WAV sound effects separately through `Encore.App.csproj`; they are outside this
manifest and these totals. Conversely, its build manifest includes the unused animation
libraries under `animations/`, even though the current app copies only `catalogue/`, `levels/`,
`environments/` and `characters/`. The measurement follows the approved manifest-based pack
policy, not a reachability analysis of the currently playable content. Neither build lists
standalone KTX2 files, so these percentages do not predict savings for a texture-heavy game.

| Chunk size, level 3 | ENCORE complete pack | Reduction | Shining Pie complete pack | Reduction of packable files |
| --- | ---: | ---: | ---: | ---: |
| 64 KiB | 10,867,709 B | 81.255% | 1,431,873 B | 70.177% |
| **256 KiB** | **10,565,005 B** | **81.777%** | **1,403,933 B** | **70.759%** |
| 1 MiB | 10,454,133 B | 81.968% | 1,361,204 B | 71.649% |

For ENCORE, 1 MiB saves another 110,872 bytes (1.05% of the default pack), while a cache miss
can require decoding four times as much data. The default 256 KiB is unchanged. ENCORE's
default pack has 2,740 compressed chunks and 338,521 bytes of container metadata (3.20% of
the final pack). All of these particular chunks benefited from compression; stored-chunk and
KTX2 behavior are covered by the tests and SafariPark corpus below.

ENCORE breakdown at 256 KiB; each row includes its own path and chunk metadata. The shared
header, footer and two TOC counts add another 96 bytes to the total.

| Category | Files | Decoded bytes | Payload + entry/chunk metadata | Reduction |
| --- | ---: | ---: | ---: | ---: |
| Meshes / skinned meshes | 385 | 37,542,560 | 6,406,812 | 82.935% |
| Animations / skeletons | 266 | 12,147,730 | 2,385,573 | 80.362% |
| Documents / materials | 2,004 | 5,396,960 | 1,045,746 | 80.623% |
| Navigation | 3 | 1,251,008 | 403,159 | 67.773% |
| Build manifest | 1 | 1,638,731 | 323,619 | 80.252% |

Reproducibility hashes:

```text
ENCORE source manifest SHA-256:
eaf68ae7951185707949f6ac0c63b0a5cee580ace09d7b181eeac3b5ee59aa00
ENCORE assets-256KiB.pak SHA-256:
54525b7344b52833e08ad2dc4411a01e310e31670073644a952bdf8dea7ca57b
Shining Pie source manifest SHA-256:
d70c462f1f570998e34e05fa2c424d0ffff735391b3b8f8ee3758cfde3642065
Shining Pie assets-256KiB.pak SHA-256:
d5ede93d8cee18d9a6e4e03428f66b38c4fba4404c511a2a5eee413e8842c7c4
```

The `--build` benchmark mode writes `compression.txt`, per-file TSV accounting, a category
summary and all three packs. The real-project artifacts are in `.scratch/asset-pack-encore`
and `.scratch/asset-pack-shiningpie`. Write times in those reports are single runs including
storage and JIT effects; they are not controlled compression-speed benchmarks.

## Executed checks

The PR was adapted to `main` at `a12f735` after the subsystem-folder move and incremental-build
changes. Review follow-up `fb20a59` reran Packing and Pipeline in Debug; their counts below
include publication rollback, recovery retention, unchanged-pack reuse, missing-path timestamps,
and replacement with an active Windows reader. The other project counts are the earlier Release
runs. SafariPark host checks used compatibility commit `0bca70b`, which preserves its older
rendering API. Windows NativeAOT storage/memory and Release browser WASM AOT smoke were rerun
after the reader changes with the same fixture and SHA-256. The recorded compression and latency measurements retain their
original corpus and results; the latency measurements predate the added footer check on open.

- Packing: 41 tests passed. Includes independent physical-file readers, cancellation after
  partial source reads, one-chunk reads/cache reuse, seek/EOF/boundary cases, stored and
  compressed data, Zio mounts/copies, disposal, corrupt metadata, hashes and frame sizes.
- Pipeline: 623 passed; 8 skipped for existing Unix-only or missing-KTX-tool conditions.
  New checks cover deterministic incremental packs, changed/deleted outputs, packed-to-loose
  switching, Wwise exclusions, GUID/size/hash preservation, injected publication failures,
  forbidden metadata outputs, failure after destination truncation, failed rollback retaining
  both recovery files, and physical replacement while an old reader remains open. Unchanged
  builds avoid pack writes; missing packs and corruption preserving size/mtime are repaired.
- Project manifests: 114 passed, including acceptance of `pack = true`.
- CLI: 189 passed; 4 fail in the unchanged Windows symlink test fixture. Its
  `HostSessionTests.SymlinkedTree.PhysicalDirectory` calls `ResolveLinkTarget(true)` on `C:\`,
  which throws `DirectoryNotFoundException` before those tests reach the CLI. The root-path
  call was also reproduced independently. These failures are not hidden or counted as passes.
- SafariPark desktop and browser hosts: both build with zero warnings/errors.
- Packing NuGet package: created successfully with its README and runtime dependencies.
- Windows NativeAOT smoke: passed from storage and immutable in-memory bytes.
- Release WASM AOT smoke: passed in headless Edge, including managed compression and decoding.
  The page reported `PACK-OK` and the matching SHA-256 in the captured DOM. Edge lingered after
  dumping the result and was stopped by the harness; its isolated profile and output were retained.
  The headless run's virtual clock is unsuitable for timing measurements.

[GitHub CI on `fb20a59`](https://github.com/ParadiseEngine/ParadiseEngine/actions/runs/38908174411)
passed the solution build, 4,142 tests (270 skips, zero failures), hosting/CLI concurrency checks,
and both managed ECS NativeAOT regression runs.

Both smoke hosts verified 160 assets / **17,822,034 decoded bytes**, with identical fixture bytes:

```text
smoke.pak length: 8,870,951 bytes (includes TOC, footer and validation manifest)
SHA-256: 2297fb4b3a846e647f016495a0194659867c230b3925fdd751c2691a3f095ef6
```

The cooked corpus contains 20 meshes, 107 skeleton/animation archives, 22 documents (generated
TOML plus empty/UTF-8 path fixtures), nine distinct KTX2 textures extracted byte-for-byte from
the GLBs (deduplicated by SHA-256), and two small checked-in KTX2 fixtures. Two of the 22 source
GLBs are excluded from mesh/animation cooking: `muledeer.glb` uses unsupported cubic animation, and
`ranger.glb` has two skins. Stale embedded data-URI copies of GLB buffers were normalized using
the same convention as SafariPark. All 22 original GLBs are included byte-identically in the
separate `safari-assets.pak` callback example (26,948,337 bytes).

## Warm desktop measurements

These are exploratory measurements over in-memory filesystems, median of seven runs after
two warmups, with the same public Zio `ReadAllBytes` API on each side. They measure storage
reading/decompression/hash validation, excluding application parsing, disk, network and GPU
upload. They are not end-to-end game or browser loading benchmarks.

| Corpus | Files | Decoded bytes | Complete pack bytes | Loose read ms | Packed read ms | Loose managed allocation | Packed managed allocation | Random read µs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Meshes | 20 | 5,715,640 | 1,734,480 | 1.951 | 39.826 | 21,677,416 B | 21,679,376 B | 357.660 |
| Skeletons/animations | 107 | 5,940,880 | 960,072 | 2.214 | 43.586 | 15,683,160 B | 15,693,376 B | 16.579 |
| Documents | 22 | 1,944 | 3,905 | 0.030 | 0.129 | 12,000 B | 14,072 B | 0.445 |
| KTX2 textures + fixtures | 11 | 6,163,570 | 6,165,699 | 3.158 | 7.951 | 22,283,872 B | 22,284,272 B | 65.023 |

Random reads use one stream per nonempty asset and 16 deterministic seeks, reusing its one
decoded-chunk cache. Each reads up to 4 KiB (shorter near EOF). Open/dispose costs are included
and divided over those reads; many small animations stay inside one cached chunk. Average
managed allocation per random operation was 12 B for meshes/animations/documents and 9 B for
textures. Allocations exclude unmanaged decoder memory and reused pooled buffers.

The mesh pack is about 30% of decoded size and the animation pack about 16%. Reading cached
loose bytes is substantially faster in this measurement: decoding and hashing cost CPU. Tiny
files grow because of per-file/chunk metadata; raw KTX2 gets container overhead with no outer
compression saving. This texture sample covers storage reads and hashes of existing KTX2
bytes, not transcoding or GPU upload performance.

`ReadAllBytes` still allocates complete decoded assets through Zio. Chunking enables bounded
stream reads and seeks, but does not reduce those existing readers' total output allocations.
A cache miss can decode 256 KiB to satisfy a 4 KiB read. Streaming consumers should benchmark
their access patterns and chunk sizes on target hardware.

Commands and harness locations are in [asset-packs.md](asset-packs.md#validation-and-benchmarks).
Generated packs, the CSV-like report, native executable, published browser files, browser DOM
result and NuGet artifact were kept under the workspace's `.scratch/asset-pack-*` directories.

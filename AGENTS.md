# ParadiseEngine agent guide

A .NET 10 / C# 14 game engine with ECS, WebGPU rendering, behavior trees and an asset pipeline.
This is the canonical agent guide; `CLAUDE.md` imports it.

## Build and test

```bash
dotnet build ParadiseEngine.slnx
dotnet test --solution ParadiseEngine.slnx --output normal
dotnet build src/Paradise.BT/Paradise.BT.csproj
dotnet test src/Paradise.BT.Test/Paradise.BT.Test.csproj --output normal
dotnet run --project src/Paradise.BT.Sample/Paradise.BT.Sample.csproj
```

Use .NET SDK 10.0.400+ (`global.json`, `rollForward: latestMinor`). Older compilers cannot load
the Roslyn 5.9 analyzers (CS9057). Shared properties and package versions live in
`src/Directory.Build.props` and `src/Directory.Packages.props`.

`Paradise.BT.Sample` enables `PublishAot` to check tree construction and ticking. Tests allow
`Reflection.Emit` for the analyzer harness. BT serialization and BLOB's
`ManagedBlobAssetReference` still need dedicated AOT publish-and-run coverage.

### Concurrent code

Changes to locks, shared flags or queues require systematic tests in the matching Coyote suite;
stress tests alone do not cover interleavings. Suites are standalone runners, skipped by
`dotnet test`: ECS, Rendering.WebGPU, Assets.Pipeline, Assets.Project, Cli, Ui.ImGui and Features.

```bash
# Rewriting runs only in Release and requires the coyote CLI.
dotnet build src/Paradise.Rendering.WebGPU.CoyoteTest -c Release
dotnet run --project src/Paradise.Rendering.WebGPU.CoyoteTest -c Release -- 200
```

- Lock on `object`: Coyote 1.7.11 rewrites `Monitor`, not `System.Threading.Lock.EnterScope`.
- Separate managed coordination from native calls; `CaptureQueue` is the renderer example.
- Await joins; `Task.WaitAll` can appear as a deadlock. Keep hang detection enabled.
- For regressions, reintroduce the defect, confirm the test fails, then restore the fix.
- Zio `CopyFileCross` resolves to the physical filesystem and bypasses wrapper `OpenFileImpl`.
  Memory filesystems can permit operations the OS refuses; see `.claude/lessons.md`.

## Coordinates

The data contract is **right-handed, Y-up, −Z forward, +X right**, in meters, with column-major
matrices. Consume Godot/glTF values without a Z mirror. ECS and rendering core are otherwise
coordinate-agnostic; conversions belong where transforms, projections or navmesh geometry form.

## Behavior trees and blobs

`Paradise.BLOB` provides unmanaged blob builders with no external dependencies. `Paradise.BT`
builds the runtime on it; `Paradise.BT.Sample` demonstrates usage and `*.Test` holds TUnit tests.

1. `[Builder]` generates builders; `LeafNode<T>`, `DecoratorNode<T>` and `CompositeNode<T>` also
   compose a `BTreeNode` graph directly.
2. `BTreeNode.Build()` validates arity (leaf 0, decorator 1; no attribute implies leaf) and
   compiles a shared `BehaviorTreeLayout`: end indices, GUIDs, aligned data offsets (up to 16)
   and defaults. `BehaviorTrees.Compile<TTree>()` returns its typed form. Trees compile from code.
3. Instances use caller-owned state/data buffers: `BehaviorTreeRef` for spans or
   `FixedBehaviorTree<TTree, TStates, TData>` for inline component storage. Pass the blackboard
   to each `Tick`; generated blackboards may be `ref struct`.
4. `VirtualMachine.Tick()` dispatches by node GUID through `NodeTypeRegistry`; the generator
   emits per-assembly registration in a module initializer.
5. Generated `IBlackboardFor<TTree>` bindings make mismatched tree/blackboard types compile errors.

Custom nodes are unmanaged structs implementing `INode`, identified by `[Guid]`, optionally
with `[Builder]`. `Tick<TBehaviorTree, TBlackboard>` permits `ref struct` arguments; `Reset` is
optional. Read node data through `blob.GetNodeData<MyNode>(index)` and shared state through
`bb.GetData<T>()`. `IBlackboard` uses `HasData`/`GetData`/`SetData`, without ref returns, so
read/write intent is statically checkable. `NodeState.None` means never ticked or reset.

BLOB's `BlobArray`, `BlobString`, `BlobPtr` and builders use relative pointers. Access every
`BlobArray`/`BlobString` through a **mutable `ref`**: copying a header, including through a readonly
reference, redirects its relative offset into the stack. In hot loops, take `field.ToSpan()` once.

## Rendering

### Compute ray tracing and probe GI

`PbrScene.Gi.Enabled` enables runtime probe lighting; `PbrGi.Volume` can override the static-scene
bounds. This uses WebGPU compute, without baking or hardware ray tracing:

- `Paradise.Geometry` builds binned-SAH BVHs, collapsed to eight-wide `BvhNode` records (96 bytes)
  with conservative 8-bit bounds. `Common/bvh.slang` mirrors the layout; CPU traversal is the oracle.
- `TraceScene` merges nodes, triangles and vertices into one buffer per kind, rebasing primitive
  indices on upload. Its per-frame top-level BVH uses opaque `PbrGiMode.Static` instances in leaf
  order and rebuilds only when a frame traces.
- `ProbeGiFeature` traces rays, blends ping-ponged octahedral irradiance/distance atlases and
  relocates/classifies probes. Irradiance tiles are 8×8, distance tiles 14×14, each with a one-texel
  wrap border. Hits use current lights/shadows and previous probes for multiple bounces; misses
  use sky ambient. `pbrCore.slang` samples probes inside the volume with Chebyshev visibility.
  `RayTracedAoFeature` provides a simpler visible tracer check.

Preserve these contracts:

- Slang retains unused globals from includes. Compute shaders should include `Common/lighting.slang`
  rather than raster `pbrCore.slang`; inspect `obj/…/shaders/<name>.reflection.json` bind groups.
- Layout entries must include compute visibility. Name-based overrides inherit the file default.
  Dawn can report a dropped dispatch asynchronously; verify a compute pass with a changed image.
- Declare compute dependencies through `AddComputePass`, `GraphBinding.StorageTexture` (write),
  `ImportBuffer` and `GraphBinding.TrackedBuffer(..., write:)`. Private outputs with no readers
  are culled. Reading last frame's atlas needs no writer in the current frame.
- Probes and sky use **E/π**, a cosine-weighted radiance mean. Direct hit lighting follows raster's
  no-1/π convention; sky carries exposure, which must not be applied again on the probe path.
- The depth/normal prepass declares the **whole vertex stream**; reflection derives stride from
  the struct even when the shader reads only position and normal.
- Forward+ `lightCull.slang` stays independent of `lighting.slang`: one thread per froxel tests
  view-space light spheres without atomics. Upload slice boundaries because WGSL `pow` and
  `MathF.Pow` may differ by an ULP and change boundary masks.
- Keep CPU oracles (`ClusterBinning`, `BvhTraversal.ClosestHit`). Test binning against brute-force
  inclusion and ensure masks are not all full. Attenuation is zero at/beyond range, so correct
  binned/unbinned frames are bit-identical; pass-matrix pixel goldens check this.

### Profiling

Build with `-p:ParadiseProfiling=true` to enable `PARADISE_PROFILING` timestamps, CPU phase laps
and the sample benchmark. API members remain present otherwise but report no timings.
`WebGpuRenderer.PassTimingEnabled` and `ReadPassTimings` use `FrameGraph.LivePassNames`.

Run the sample's `--gi-demo --bench`; `--pbr --bench` reports nothing. On Apple GPUs, overlapping
passes inflate per-pass wall times: trust the idle-to-idle GPU frame total and compare feature
toggles (`--no-gi`, `--rtao`, `--no-bloom`, `--gi-rays`, `--gi-max-probes`, `--gi-probes-per-frame`,
`--lights`). Workgroup ray staging, any-hit traversal and half-resolution AO helped; nearest-first
child sorting cost more than it saved. Verify optimization images as well as frame totals.

`LightCull.Bin` scales linearly: on an Apple M-series at 1280×960 it measured 0.039, 0.093,
0.312 and 1.229 ms at 64, 256, 1024 and 4096 lights. Two-level culling and per-tile workgroups
were no faster; raising the tile-list capacity from 256 to 2048 also had no effect. Measure
this pass separately before restructuring it: its current frame cost is small.

## Feature configuration

Every switchable subsystem declares a `FeatureDefinition` and reads `IFeatureSwitches`.
`Paradise.Features` has no package dependencies; `Paradise.Features.Toml` isolates Tomlyn from
ECS consumers. Keep this name: `Paradise.Configuration` would shadow Coyote's `Configuration`.

Overrides merge in order: declarations, `engine.toml`, environment (`PARADISE_FEATURES`), then
CLI (`--features +a,-b`). `FeatureId` is a validated dotted name. Overrides may precede declaration;
retain unknown names in `FeatureSwitches.Unknown` so hosts can report typos.

```toml
[[features]]
name = "rendering.globalIllumination"
enabled = false

[[features]]
name = "game.weather"
enabled = true
intensity = 0.6
windMetresPerSecond = 3.5
```

- Use one `[[features]]` entry per feature; `name` and optional `enabled` are reserved. Reject
  duplicates. Other keys are settings; later layers replace the entire settings table.
- Carry settings as text and bind through the producing reader (`FeatureSettingsToml.Read<T>`)
  with the caller's source-generated `TomlSerializerContext`. Do not convert to JSON or reflect.
- Settings use `get; set;` and a context naming policy. `init` can lose omitted-value initializers;
  without `PropertyNamingPolicy`, camelCase keys may not bind. `FeatureSettingsTests` covers both.
- Require host-provided switches in `PbrRenderer` and `RenderPipeline`; an unconfigured host
  explicitly passes `new FeatureSwitches()`.
- Both the build switch and scene `Enabled` must allow a feature. Scene-disabled bloom may still
  declare a chain for graph culling; a disabled switch prevents the feature from running.
- Snapshot switches once per frame/schedule run. Mid-run changes apply next time. Do not subscribe
  the pipeline to `Changed`, which may run on another thread and race GPU state; hosts can call
  `BeginFrame` for an immediate transition before the next frame.
- Persistent render state implements `IRenderFeature.OnEnabledChanged`: retract shadows, SSAO,
  probe volumes and froxels on disable, including when already disabled at `Add`.
- Register engine features only in `PbrBuiltInFeatures`; games add features at spaced
  `PbrFeatureOrder` slots. Keep feature APIs on their features, without renderer forwarding or
  `…ForTest` accessors. The renderer exposes frame/output state and uploads.
- Upload buffers filled during graph recording in `BeforeSubmit`, after `Setup` and compilation.
- Serialize switch writes and `Changed` notifications in one critical section to preserve order;
  `Paradise.Features.CoyoteTest` covers this.

List declarations with `dotnet run --project src/Paradise.Rendering.Sample -- --list-features`.

## Logging and filesystems

Libraries take `ILogger` and depend only on `Microsoft.Extensions.Logging.Abstractions`; hosts
choose providers. Use `[LoggerMessage]` on partial classes for checked, allocation-free disabled
logging. Logger parameters are non-nullable; use `NullLogger.Instance`. Pass `UPath` values intact
so hosts can render them through `ParadiseConsoleOptions.RenderValue`. Sinks must be thread-safe
for Dawn, Noesis and SDL callbacks. `Console` is reserved for program output such as verb results
and schema dumps, never library diagnostics.

Content readers take Zio `IFileSystem` and `UPath`, including pipeline/documents, `AuthoredDocument`
and Noesis XAML/texture/font providers. Hosts select archive, play-tree or memory mounts.

- Keep `/` separators verbatim. Let the mount enforce containment; confine untrusted relative
  URIs, such as GLB images, with `SubFileSystem` over the source directory.
- Use `MemoryFileSystem` in tests; mount disk fixtures relative to the test output directory.
- Wwise's native bank loader requires host paths; document equivalent exceptions explicitly.

## Asset pipeline

### Identity and outputs

`AssetReference` is `{ guid, path }`: **GUID identifies; path is a readable hint**.
Pass `AssetReference` across reference APIs, not a raw path.
Share one `AssetIndex` scan across build, bake, resolve, verify and repair. Resolve with
`AssetIndex.AssetOf`, never `assetsRoot / reference.Path`; key caches and cycle checks by GUID.
Stale paths after external renames are warnings, repairable with `verify --fix`; missing GUIDs are
errors. `assets mv` updates hints eagerly while retaining sidecar identity.

A GLB is source only and builds no output. Tool-owned `.mesh`, `.skinnedmesh`, `.skeleton` and
`.anim` documents name its parts with `{ source, slot, name, index, hash, skeleton }`. Prefabs
reference those documents, not the GLB. Meshes cook to aligned native MeshBlob data (magic/version
first); skeletons and clips cook to ozz archives. Clip lookup uses name, then content hash, then index.

The GLB determines rigid versus skinned kind. A skinned document names its `.skeleton`; MeshBlob
v3 stores that skeleton's **built path**. Kind mismatches are build errors; when the GLB gains or
loses its rig, replace the stale document with a fresh identity.

`ImportContext.BuiltPath` asks the referenced asset's own importer where output lands. Textures
become KTX2, prefabs/configs use the profile extension, and mesh/skeleton/clip/material/audio/binary
retain their paths. Built `.material` uses TOML or JSON by profile, detected by its first character.
Both prefab and material baking use this API; runtime readers never derive paths by convention.

### Animation contracts

`Paradise.Animation` is a managed ozz-animation port pinned to 0.17: `ozz-skeleton` v2 and
`ozz-animation` v7. Archives are persisted; runtime blobs are not. Builders/optimizer/converter
are managed; runtime skeletons, animations, poses and sampling contexts are unmanaged BLOB layouts.

`SamplingContext.Sample` and `LocalToModel.Compute` allocate nothing. Four-track `Vector128`
interpolation retains ozz's SoA layout; cursor walking remains scalar and variable-index lane
extraction uses a stack store. `AnimationPlayer` keeps clip references and playback/fade state in
the class; one native blob per character owns sampling contexts, poses and matrices. Hosts call `Advance` then
`Evaluate`, and use `SkinningPalette.Compute` for GPU palettes. Use `JointPoses` batch operations
in hot paths; its indexer gathers one joint for attachments/tests.

The skeleton contains the GLB's whole node tree in depth-first, parents-first order, with siblings
ordered by glTF index. Mesh skins map palette slots to joints and inverse binds. Unanimated joints
hold rest pose. `ClipConverter` fills it before the ozz builder's identity padding, bakes STEP holds
and inserts slerped keys for rotation arcs wider than 15°. Quantization is 16-bit; optional sidecar
`[glb] optimize = { tolerance, distance }` uses ozz's 1 mm / 10 cm defaults.

`OzzParityTests` compares generated archives byte-for-byte against native fixtures. The glTF
reference sampler lives only in pipeline tests. `Paradise.Animation.Benchmarks` compares blob,
managed and glTF runtimes; `PARADISE_OZZ_NATIVE` selects the native shim and
`PARADISE_BENCHMARK_GLB` selects a character asset.

### Importers and extraction

- `ImporterChain` alone walks importers. `Claims` reads only path/header data; sidecar creation
  records the chosen importer. Never overwrite a recorded name or fall back from an unknown name.
  Keep importer extension guards for hand-edited sidecars; a declined import is a build error.
- Builds must not edit committed sidecars to choose an importer. Build-time reconciliation uses
  `RewriteSources = false`: sidecar identity repair may not move authored paths or container URIs.
- Watchers mint tool-owned GLB part documents; `extract` may overwrite a stale part belonging to
  that GLB, never one belonging to another. Materials, textures and prefabs become authored when
  created and only `extract` writes them.
- Sidecar extraction fingerprints track both container and document state; material fingerprints
  cover only glTF-expressible fields. Refuse extraction when both sides changed.
- KTX2 is build output only; `verify` rejects it beneath `assets/`.
- Read sidecar inline tables through both `CanonicalTomlTable` and `CanonicalInlineTable` because
  root-level inline tables may deserialize as generic tables.

### References

`ReferenceGraph` is derived from `AssetIndex` and importer-declared `References`, never persisted.
Document reference lists stay in documents. Container references live in `MeshImportSettings`
sidecar data because source containers cannot always be rewritten. Preserve edges to missing
identities and their paths so diagnostics can identify their referrers.

- `mv` follows `DependentsOf` plus `Unreadable` assets through the importer rewrite API.
- `rm` refuses referenced assets unless forced; it never clears a reference slot.
- Watchers follow moved identities' dependents, deferring and retrying files still in debounce.
- Use `ReferenceChain` and `IAssetImporter.Rewrite`; verbs must not branch on extensions.
- Unrecorded container URIs remain `PathOnly`; moves follow only those they touched and report
  unresolved cases without rewriting unrelated sources.

## Style and Git

`.editorconfig` is enforced with warnings-as-errors: Allman braces, four spaces, file-scoped
namespaces, LF; `_camelCase` private/internal fields, `s_camelCase` statics and PascalCase constants
and public members. Prefer keyword types, omit `this.`, and preserve zero-allocation unmanaged
paths with structs, `ref` and `Unsafe` where required.

Return empty collections instead of null, using shared instances such as `AssetReferences.None`.
Null represents an absent single object. Comments explain constraints, decisions, failure modes
and cross-language contracts; delete control-flow narration. XML summaries are one sentence;
remarks hold rationale. Prefer clear names and small methods over explanatory prose.

Feature branches start from `main`; PRs go to quabug and squash-merge. Issue fixes put
`Closes #NNN` at the top of the PR body and in the commit message, one line per issue; use
`Towards #NNN` only for deliberately partial work. Never commit or push unless asked.

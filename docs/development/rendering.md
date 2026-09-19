# Rendering

Runtime renderers consume cooked geometry spans, engine-owned material descriptions and standalone
KTX2 inputs; source-container import stays in build tooling. See [runtime render assets](runtime-render-assets.md)
for the API migration, ownership boundary and cooked upload examples.

### Resource ownership

Resource release runs on the render thread between frames, after all recorded users have been
submitted or discarded. Public handles become stale immediately; WebGPU keeps submitted work safe
without a presenting-frame delay. PBR geometry, materials and custom programs have separate
lifetimes: retire instances before geometry/materials, then materials before their programs and
caller-owned extra bindings. See the [PBR lifetime contract](../../src/Paradise.Rendering.Pbr/README.md#resource-lifetime)
for `ReleasePrimitive`, `ReleaseMaterial` and `ReleaseMaterialProgram` ownership and sharing rules.
Storing an `IDisposable` object as an ECS managed component does not make ECS removal dispose it;
the owning subsystem must release native resources explicitly before removing their references.

Native and browser backends share the internal `RefCountedCache<TKey, TValue>` in
`Paradise.Rendering`. Leases keep entries alive until the final release; `Clear` invalidates all
current leases but permits new acquisitions, while `Dispose` permanently closes the cache.
Bulk cleanup retires all entries before callbacks, attempts every release, and aggregates failures.
The browser shader adapter owns WGSL keys and JS slot recycling; only successfully released slots
return to its free list. Cache and lease operations stay on the owning render thread.

### Feature data

PBR features exchange frame results through `FrameBlackboard`, without references or callbacks
to sibling features. Texture results use named `GraphTexture` entries; typed data uses shared
`FrameDataKey<T>` keys. Publish once per frame and read with `TryGet` or `GetOrDefault`. Keys use
instance identity and should be declared once. Clearing the board removes result presence and
releases reference values while retaining typed storage, so struct publications do not box.
The board borrows GPU handles and frame data; it does not own or dispose their resources.

Missing required inputs skip their consuming passes. Optional shadows, light grids, decals and
probe lighting use neutral resources owned by the renderer, independently of their optional
producers. Consumers must not retain results across frames or use a previous producer's data
when its feature is disabled.

`FrameLightingFeature` prepares and uploads shared lighting uniforms once, after light culling
and before probe GI, and publishes `FrameLightingData`. Scene, GI and fog consume that result;
they skip dependent work when it is absent. Lighting no longer depends on the scene pass running.
Light culling runs at `PbrFeatureOrder.LightCulling` (450), frame lighting at
`PbrFeatureOrder.FrameLighting` (475), and probe GI at `PbrFeatureOrder.GlobalIllumination` (500).

Publish graph resources as graph handles and bind them with tracked reads. In particular,
light-grid consumers use `GraphBinding.TrackedBuffer`, keeping `LightCull.Bin` alive through its
actual consumers rather than `NeverCull`. Publishing a handle does not itself create an edge.
Probe GI updates persistent history, so its required history work remains live independently
of whether a current scene pass samples the result.

### Frame extraction and instancing

`PbrRenderer.RenderFrame` captures instance transforms, rendering flags and `scene.Instancing`
after feature `PrepareFrame` callbacks, before `Setup`. Finish instance and joint-palette changes
before that boundary. Feature switches are snapshotted earlier, at `BeginFrame`. Changes during
`Setup` take effect in a subsequent frame. All raster passes consume the captured values,
including motion history.

`DrawPreparationFeature` selects direct or packed-and-regrouped preparation during `Setup`,
after draw extraction and trace geometry construction. Its `PbrFeatureOrder.DrawPreparation`
slot (-50) precedes `PbrFeatureOrder.First` (0), so game features using the normal order slots
see prepared draws, as do frustum culling and the later passes. The
`PbrFeatures.DrawPreparation` switch (`rendering.drawPreparation`) defaults to true. Disabling
it guarantees direct preparation through a renderer fallback before feature setup; preparation
cannot be disabled independently of rendering.

Object transforms are calculated once per scene instance and retained for a coherent frame.
Primitive draws reference those captured objects. By default, their values are written directly
into the aligned uniform ring without first filling a main-draw array. Main-pass instance staging
is allocated and filled only when an actual batch needs it. Ordinary draws and unbatched
prepasses use the uniform-ring slots. Instanced depth and shadow passes build their own compact
arrays from captured object values, without requiring main-draw packing. Culled main draws keep
their slots. The 208-byte shader layout and custom material bindings remain unchanged.

Consecutive compatible draws instance automatically. To opt into canonical packed main-draw
storage and also group nonconsecutive eligible opaque draws, set
`scene.Instancing = new PbrInstancing { PackAndRegroup = true }`. This combined option defaults
to false and requires scene instancing, `rendering.instancing` and `rendering.drawPreparation`
to be enabled.
Its contiguous `DrawUniformsGpu` array follows final draw order and can be uploaded directly for
main-pass instancing, alongside the uniform ring. Packing does not eliminate duplicate GPU
uploads, and its CPU cost can outweigh draw savings; measure it on the target scene.
Both paths share one `Frame.Draws` array: eager packing fills it for the opt-in path, while the
default path fills it only for an actual main batch. Capacity survives later frames, but frames
without either need do not refresh it.
Regrouping can also change which material wins at equal depth. Custom programs without explicit
reorder permission and alpha-masked materials form boundaries that grouping never crosses;
transparent draws retain back-to-front order.
Compatibility includes the complete primitive descriptor and effective skinning mode.

Custom program registration accepts `MaterialProgramOptions`. `PreservesMeshBounds` enables
camera culling for rigid, static primitives. `AllowsOpaqueReordering` permits regrouping when
the scene opts in. `OpaqueCoverage` separately guarantees stock geometry without displacement
or fragment discard for occlusion; a roof dissolve can preserve bounds without solid coverage.
Unspecified capabilities retain the conservative behavior.

Provide `InstancedVertexEntryPoint` to instance a custom material. Include
`Common/pbrInstancing.slang` and call `pbrTransformVertexInstanced(input, instanceId)`.
An ordinary fragment entry can be reused if it does not read the single-draw uniform. Otherwise
provide `InstancedFragmentEntryPoint` accepting `InstancedFragmentInput`. Its `surface` holds the
ordinary fragment input; `pbrInstanceDraw(input.instanceDrawIndex)` supplies the model/flags.
Material bindings, including per-room dissolve buffers, remain part of batch compatibility.

Depth/normal and shadow passes also batch compatible geometry, independently of main materials
and the `PackAndRegroup` option. Custom-material instancing and visibility culling likewise remain
available with this option or the draw preparation feature disabled.
The prepass combines consecutive geometry in the final main order; shadows group by geometry.
They retain full vertex strides and per-instance transforms/joint offsets. Camera visibility
applies to the prepass; shadows use each light view's frustum and retain uncertain/animated
bounds so offscreen shadow casters are not lost.

Trace geometry is built before raster regrouping, retaining submission order so identical
visible and GI geometry can share their hierarchy. Frustum and occlusion indices and main-pass
`FirstInstance` refer to the final raster order; instanced depth/shadow offsets refer to each
pass's compact array. GPU occlusion still
uses individual indirect draws; regrouping does not combine those commands.

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
- Orthographic froxels keep constant tile XY bounds across depth; perspective tiles expand.
  Both use `ViewDepthMapping`: `viewXY = origin(ndc) + depth * slope(ndc)`, with coefficients
  derived once from the validated projection and uploaded as two `float4`s. CPU and GPU binning
  share this geometry without a projection-kind flag or inverse-projection matrix. Off-center
  and jittered cameras are supported; XY mixing and oblique depth terms are rejected.
  Unsupported or infinite depth ranges fall back to all lights. Fog uses the current ray sample's
  slice and falls back outside the grid, retaining directional and unbounded lights.
- Keep CPU oracles (`ClusterBinning`, `BvhTraversal.ClosestHit`). Test binning against brute-force
  inclusion and ensure masks are not all full. Attenuation is zero at/beyond range, so correct
  binned/unbinned frames are bit-identical; pass-matrix pixel goldens check this.

### Profiling

Build with `-p:ParadiseProfiling=true` to enable `PARADISE_PROFILING` timestamps, CPU phase laps
and the sample benchmark. API members remain present otherwise but report no timings.
`WebGpuRenderer.PassTimingEnabled` and `ReadPassTimings` use `FrameGraph.LivePassNames`.

Run `src/Paradise.Rendering.Sample` in the sibling
[ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples) repository with `--gi-demo --bench`; `--pbr --bench` reports nothing. On Apple GPUs, overlapping
passes inflate per-pass wall times: trust the idle-to-idle GPU frame total and compare feature
toggles (`--no-gi`, `--rtao`, `--no-bloom`, `--gi-rays`, `--gi-max-probes`, `--gi-probes-per-frame`,
`--lights`). Workgroup ray staging, any-hit traversal and half-resolution AO helped; nearest-first
child sorting cost more than it saved. Verify optimization images as well as frame totals.

Historical measurements (2026-09-07): `LightCull.Bin` scaled linearly on an Apple M-series
at 1280×960, measuring 0.039, 0.093,
0.312 and 1.229 ms at 64, 256, 1024 and 4096 lights. Two-level culling and per-tile workgroups
were no faster; raising the tile-list capacity from 256 to 2048 also had no effect. Measure
this pass on the current scene and hardware before restructuring it; these timings are a baseline,
not a current performance guarantee.

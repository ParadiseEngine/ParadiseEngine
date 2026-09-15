# Rendering

### Frame extraction and instancing

`PbrRenderer.RenderFrame` captures instance transforms and rendering flags after feature
`PrepareFrame` callbacks, before `Setup`. Finish instance and joint-palette changes before
that boundary. All raster passes consume the captured values, including motion history.

Object transforms are calculated once per scene instance. Primitive draws reference those
objects, then project into a contiguous `DrawUniformsGpu` array in final draw order. Instancing
uploads that array directly; ordinary draws and unbatched prepasses use an aligned uniform-ring
copy with the same indices. Instanced depth and shadow passes build their own compact arrays.
Culled main draws keep their slots. The 208-byte shader layout and custom
material bindings remain unchanged.
Frames that instance also upload the storage array alongside the uniform ring. This stage
reduces repeated transform work and draw encoding, not per-draw GPU upload bandwidth.

Consecutive compatible draws instance automatically. To also group nonconsecutive eligible
opaque draws, set `scene.Instancing = new PbrInstancing { ReorderOpaque = true }`. Both the
scene setting and the instancing feature switch must be enabled. Reordering is opt-in because
it can change which material wins at equal depth. Custom programs without explicit reorder
permission and alpha-masked materials form boundaries that grouping never crosses; transparent
draws retain back-to-front order.
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

Depth/normal and shadow passes also batch compatible geometry, independently of main materials.
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

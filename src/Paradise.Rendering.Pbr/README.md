# PBR visibility

Camera frustum culling runs by default. GPU occlusion is opt-in per scene:

```csharp
scene.Visibility.FrustumEnabled = true;
scene.Visibility.OcclusionEnabled = true;
```

The engine switches `rendering.frustumCulling` and `rendering.occlusionCulling` must also
be enabled. Both default to enabled; the scene's occlusion setting defaults to false.
Switch changes take effect at the next frame, including removal of previously computed
visibility. The features are available through `renderer.Pipeline.Find<T>()`.

Frustum culling tests the eight transformed local bounds corners against the six WebGPU
clip planes. It skips camera and normal/depth prepass draws, keeping the full scene for
shadow maps and probe/ray-traced lighting. Transparent draws retain their original
back-to-front sorting and all draws retain their uniform-ring slots.

GPU occlusion uses the current camera and transforms each frame. A dedicated depth pass
renders solid stock rigid materials, then compute reduces depth to conservative 8×8 max-depth
tiles. A second compute pass tests expanded projected bounds and writes the 20-byte
`DrawIndexedIndirectCommand` arguments consumed by the opaque pass. Hidden draws get zero
instances without a CPU readback. Moving objects and camera cuts therefore need no history
invalidation or delayed visibility recovery. The graph tracks depth, tile and argument
buffer dependencies; the visibility passes disappear when their scene consumer disappears.

Unknown bounds, mutable vertex streams, skinned geometry, and custom vertex programs stay
visible. Bounds crossing the eye or near plane also bypass occlusion. Alpha cutouts,
transparent/transmissive surfaces, custom shaders and deformed geometry never contribute
occluder depth. Fully covered tiles are required: background pixels, silhouettes and holes
can reduce culling efficiency but cannot hide geometry through an uncovered pixel.

The extra depth pass and tile tests have a cost. Enable occlusion for scenes where avoiding
hidden material shading justifies that cost; it is not a claim of lower GPU time for every
scene. The current implementation emits one indirect command per original opaque draw,
without compacting commands or aggregating instances. `OcclusionCullingFeature.IndirectBuffer`
and `DrawCount` expose the current arguments for tools; `FrustumCullingFeature.CulledDrawCount`
reports CPU rejections.

`Visibility` contains the CPU references. `VisibilityTests` tests homogeneous clipping,
conservative fallbacks, actual GPU argument changes, current-frame disocclusion, partial
edge tiles after resize, feature transitions, and byte-identical output with culling disabled,
including eight jittered TAA frames with moving geometry, fog and directional shadows.
The indirect command is implemented by the native WebGPU and browser backends.

## Resource lifetime

Upload and release resources on the render thread between `RenderFrame` calls. Remove all raster
and GI users first, then submit or discard any recorded command streams that reference them.
Backend destruction invalidates handles immediately without blocking for the GPU; WebGPU preserves
already-submitted work until it completes. Offscreen-only hosts do not need to present a frame
to retire resources.

Use `renderer.UploadMesh(asset, out var materialIds)` when an uploaded asset will be unloaded
before renderer disposal. The returned meshes preserve source mesh order; `materialIds` preserves
source material order, including unused materials, and appends a fallback material only when a
primitive needs one. After retiring the asset's raster and GI users:

```csharp
foreach (var mesh in meshes)
    foreach (var primitive in mesh.Primitives)
        renderer.ReleasePrimitive(primitive);
foreach (var materialId in materialIds)
    renderer.Materials.ReleaseMaterial(materialId);
```

The original `UploadMesh(asset)` overload remains suitable for renderer-lifetime assets. Its
materials survive until renderer disposal unless explicitly released; primitive material IDs alone
do not include unused source materials.

- `renderer.ReleasePrimitive(primitive)` releases uploaded vertex/index buffers and trace geometry.
  Copies made with `with`, including material variants, share the same geometry ownership; they do
  not acquire another lease. Release once after the final user retires. Repeated release returns
  false, and geometry belonging to another renderer is rejected. Materials have a separate lifetime.
- `renderer.Materials.ReleaseMaterial(id)` releases the material uniform buffer and bind group,
  and releases each uploaded texture after its final material user retires. Extra binding resources
  remain caller-owned; release their materials before destroying those buffers, textures or views.
  Default textures and the shared sampler remain cache-owned. Unknown or released IDs return false.
- `renderer.ReleaseMaterialProgram(id)` releases a custom program's ordinary and instanced
  pipelines. Release its materials first; a live material prevents program release. Program zero
  is built in and cannot be released. Unknown or released custom IDs return false.

Material and custom-program IDs are not reused. Hosts own sharing and retention of primitives,
material IDs and program IDs; copying a descriptor does not extend its lifetime. Renderer disposal
releases resources still owned by it. Native and browser backends share shader modules and binding
layouts while their owning handles remain live, and drop cache references after the last owner
releases them.

Browser lifetime checks run without a GPU using `dotnet test --project
src/Paradise.Rendering.Browser.Test/Paradise.Rendering.Browser.Test.csproj` and
`node --test src/Paradise.Rendering.Browser.Test/ResourceLifetime.test.mjs` from the repository root.
The Node tests exercise the shipped JavaScript shim with a mock WebGPU device.

## DDGI debugging

GI settings belong to `ProbeGiFeature`, not `PbrScene`. Replace them on the rendering thread
before `RenderFrame` to tune DDGI live:

```csharp
var gi = renderer.Pipeline.Find<ProbeGiFeature>()!;
gi.Settings = gi.Settings with
{
    Enabled = true,
    ProbeSpacing = 1f,
    RaysPerProbe = 128,
    ProbesPerFrame = 256,
};

switches.Set(PbrFeatures.GiProbes.Id, true);
renderer.Pipeline.Find<ProbeGiDebugFeature>()!.ProbeRadius = 0.08f;
```

`ProbeGiDebugFeature` owns the visualization pipeline and `Gi.DebugProbes` pass. Its
`rendering.debug.giProbes` switch defaults off. Markers respect scene depth and show relocated
positions (green active, red inactive), without adding traced geometry. `ProbeRadius` sets
their size in metres. Turning off visualization leaves GI updates running. With GI disabled
through its settings or `rendering.globalIllumination`, no markers are drawn.

`ProbeSpacing` is a minimum spacing for the automatic grid; zero derives density from
`MaxProbes`. The budget and atlas limits can widen it. An authored `Volume` overrides fitting;
replace its spacing/counts to change its density. Grid changes restart probe convergence.
`RaysPerProbe` (8–256), `ProbesPerFrame` (zero updates all probes), `Hysteresis`, `Intensity`,
`NormalBias`, and `ViewBias` take effect on the next frame. The feature's `ActiveVolume` and
`ProbeCount` report the effective grid. ParadiseSamples' renderer showcase exposes these
controls in its **DDGI** panel. Migrating callers should replace `scene.Gi` assignments with
`renderer.Pipeline.Find<ProbeGiFeature>()!.Settings` assignments.

## Bounded, scrolling DDGI

A dense volume spanning an entire town spends probes on places the player cannot use.
Set `Scrolling = true` with an authored `Volume`, then move the requested origin around gameplay:

```csharp
var halfSpan = new Vector3(16, 4, 16);
gi.Settings = new PbrGi
{
    Enabled = true,
    Scrolling = true,
    Volume = new PbrProbeVolume(playerPosition - halfSpan, new Vector3(2), 17, 5, 17),
    MaxProbes = 2048,
    RaysPerProbe = 128,
    ProbesPerFrame = 128,
    UpdateFocus = playerPosition,
};

// Before each frame, on the rendering thread:
gi.Settings = gi.Settings with
{
    Volume = gi.Settings.Volume! with { Origin = playerPosition - halfSpan },
    UpdateFocus = playerPosition,
};
```

The resident grid moves only by whole cells; `ActiveVolume` reports its snapped origin. Interior
probes retain their world positions, relocation, classification and atlas tiles. Only entering
planes become invalid. Shape/spacing changes or a teleport beyond the overlap reset the whole
grid. Invalid probes cannot sample stale lighting and their first blend replaces history.
This follows the storage-reuse principle in NVIDIA's
[infinite scrolling volume design](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/DDGIVolume.md#infinite-scrolling-movement).

New and explicitly invalidated probes get update priority. With `UpdateFocus`, half the remaining
budget updates nearby probes and half maintains a background sweep; a one-probe budget alternates
between the two. Without a focus, the background sweep is round-robin. `Invalidate(bounds)`
resets history and classification within the bounds plus one probe cell. Supply the affected
lighting region when lights change; an object's own bounds alone may not cover its distant
indirect effect.

Budgeted blending and classification dispatch only selected probes. `Gi.Carry` copies the previous
selection's tiles and states only where the current selection will not overwrite them, preserving
the prior bounce without full-atlas maintenance. A full-to-budgeted transition can still carry a
large selection once. Inactive probes skip traversal but still occupy scheduled slots; GPU
compaction and convergence-driven scheduling are future work. This remains one volume, with the
existing sky fallback outside it; multiple volumes require a separate blending/coverage design.

## GI hit-light culling

GI ray hits use a conservative world-space light BVH, independent of camera froxels. Directional
and unlimited-range lights remain global; zero indirect energy is discarded before shadow work.
Candidate lights are evaluated in their original scene order, preserving shadow slots and
floating-point accumulation. `LightCullingEnabled = false` provides an unculled comparison.
The renderer's existing admission limit remains 64 lights.

An Apple M3 Max fixture with 64 distributed point/spot lights, 405 probes, 128 rays per probe,
and a 96×96 render measured median `Gi.Trace` at 0.590 ms unculled versus 0.262 ms culled.
Synchronous render plus timing readback measured 2.720 versus 2.155 ms, while CPU setup rose from
0.100 to 0.145 ms. These are 40-frame samples in alternating off/on/on/off runs after warmup;
the synchronous measurement includes CPU/readback and is not an idle-to-idle GPU frame total.
The GPU regression requires identical culled/unculled pixels. Measure representative gameplay
before extrapolating this fixture to a larger world.

## GI geometry for large scenes

`PbrInstance.GiMesh` substitutes an uploaded, simplified mesh for that instance's probe tracing.
The proxy uses the instance's transform and its own primitive materials. Instances sharing a
proxy also share its uploaded mesh BVH. Rasterization, direct shadows and ray-traced AO keep
using `Mesh`.

`scene.GiGeometry.Instances` adds geometry used only by GI, including coarse distant walls that
must continue to block sky rays after detailed geometry streams out. Set
`scene.GiGeometry.IncludeSceneInstances = false` to supply the complete participating set
explicitly. Only static, opaque or alpha-tested primitives participate, matching the existing
tracer rules; alpha tests and material textures are still approximated by material factors.
The automatic probe-volume fit uses this GI set's bounds.

```csharp
building.GiMesh = sharedBuildingProxy;
scene.GiGeometry.Instances.Add(distantOccluder);

// A host that manages the complete GI set can replace its membership each frame.
scene.GiGeometry.IncludeSceneInstances = false;
scene.GiGeometry.Instances.Clear();
scene.GiGeometry.Instances.AddRange(residentGiInstances);
gi.Invalidate(changedWorldBounds);
```

Change membership on the render thread before `RenderFrame`, and invalidate changed regions
to reclassify probes previously inside removed geometry. Tracing membership updates next frame;
release unused uploads through `ReleasePrimitive` after their last raster and GI user retires.
This API supplies participation and proxy selection; hosts own streaming, proxy creation and mesh residency budgets. GI and AO
share mesh buffers and, when their participating sets match, their instance hierarchy too.
Use an authored bounded probe volume when distant occluders should not enlarge probe coverage.

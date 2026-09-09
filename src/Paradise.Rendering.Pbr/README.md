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

## DDGI debugging

Replace `scene.Gi` on the rendering thread before `RenderFrame` to tune DDGI live:

```csharp
scene.Gi = scene.Gi with
{
    Enabled = true,
    ShowProbes = true,
    ProbeSpacing = 1f,
    RaysPerProbe = 128,
    ProbesPerFrame = 256,
};
```

`ShowProbes` draws depth-tested markers at relocated world positions (green active, red
inactive), without adding traced geometry. `ProbeRadius` sets their size in metres.
The `Gi.DebugProbes` graph pass is absent when markers or GI are disabled, including the
`rendering.globalIllumination` switch.

`ProbeSpacing` is a minimum spacing for the automatic grid; zero derives density from
`MaxProbes`. The budget and atlas limits can widen it. An authored `Volume` overrides fitting;
replace its spacing/counts to change its density. Grid changes restart probe convergence.
`RaysPerProbe` (8–256), `ProbesPerFrame` (zero updates all probes), `Hysteresis`, `Intensity`,
`NormalBias`, and `ViewBias` take effect on the next frame. The feature's `ActiveVolume` and
`ProbeCount` report the effective grid. ParadiseSamples' renderer showcase exposes these
controls in its **DDGI** panel.

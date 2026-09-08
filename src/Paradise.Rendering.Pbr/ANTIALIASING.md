# Antialiasing

> Sample commands in this document run from [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples), which now owns the sample applications.

FXAA and TAA are independently optional scene effects:

```csharp
scene.Taa = new PbrTaa { Enabled = true };
scene.Fxaa = new PbrFxaa { Enabled = true };
```

The corresponding engine switches are `rendering.temporalAntiAliasing` and `rendering.fxaa`.
Both switches default to enabled; both scene settings default to disabled. Either effect can run
alone, or FXAA can filter the remaining edges after TAA. FXAA also smooths fine texture detail,
so enabling both is a quality choice rather than a requirement.

TAA applies an eight-sample Halton offset to the frame-local projection, leaving `scene.Camera`
unchanged. Raster passes, motion vectors and local-light binning use that effective projection.
It resolves linear HDR color before bloom and tone mapping. Saved depth rejects disoccluded
history; luminance-compressed YCoCg neighborhood and variance bounds limit ghosting. Background
taps can contribute silhouette coverage only when neighboring history also matches the surface.

`HistoryWeight` controls retained history (default 0.9, clamped to 0–0.98). `JitterScale` controls
the subpixel offset (default 1, clamped to 0–2); zero keeps accumulation without camera jitter.
`DepthThreshold` is a relative view-depth tolerance, and `VarianceGamma` controls neighborhood
clamping. More retained history smooths static edges but can soften moving detail.

For a camera cut, teleport or discontinuous geometry change, increment
`scene.TemporalHistoryVersion`. This resets TAA and motion history together. TAA also resets on
resize, scene replacement, settings changes and enable transitions. Its `ResetHistory()` method
restarts only color accumulation and the jitter sequence. `HistoryReady` and `JitterPixels` are
available through `renderer.Pipeline.Find<TemporalAntiAliasingFeature>()`.

TAA requires the `rendering.motionVectors` and `rendering.scene` switches. If either is off,
TAA stops and leaves the camera unjittered. Built-in rigid and GPU-skinned motion is supported;
CPU vertex deformation and custom displacement require a matching previous-geometry motion path.
Background and transparent geometry reject temporal history conservatively. There is no reactive
material mask or special reconstruction for fast-changing shading, so these remain quality limits.

FXAA runs after tone mapping in display-linear color, using perceptual luminance to find edges.
`EdgeThreshold`, `MinimumThreshold` and `SubpixelQuality` control filtering strength. Presentation
applies the sRGB transfer once, or lets an sRGB target encode it in hardware. UI overlays render
after presentation and are not filtered. With both scene effects disabled, the original direct
composite path remains in use.

TAA retains two RGBA16F color textures and two R32F depth textures (24 bytes per pixel), in addition
to motion-vector resources. FXAA uses an RGBA16F intermediate after the display-color target.
These effect resources are created on first use; disabling skips their passes and invalidates
temporal state, while allocated resources remain available for reuse.

The sample accepts `--taa` and `--fxaa` with `--pbr`, `--gi-demo` or `--ssr-demo`:

```bash
dotnet run --project src/Paradise.Rendering.Sample -- --pbr --taa
dotnet run --project src/Paradise.Rendering.Sample -- --pbr --fxaa
```

`AntiAliasingTests` checks real geometry coverage, HDR accumulation, depth and motion rejection,
camera cuts, switches, output gamma, combined pass ordering and local-light pixel parity through
all eight jitter phases. Save its unfiltered/filtered images for visual inspection with:

```bash
PARADISE_AA_ARTIFACTS=/tmp/paradise-aa-review \
  dotnet run --project src/Paradise.Rendering.Pbr.Test -- \
  --treenode-filter '/*/*/AntiAliasingTests/*' --maximum-parallel-tests 1
```

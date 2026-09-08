# Post processing

The default scene preserves the existing bloom and tonemap path. Every new effect has a
`PbrScene` settings record with `Enabled = false` by default and an independent engine feature
switch. The renderer registers these features only in `PbrBuiltInFeatures`; games can insert
features through the same pipeline and blackboard interfaces.

```csharp
scene.DeltaSeconds = deltaSeconds;
scene.ElapsedSeconds = elapsedSeconds;
scene.Exposure = new PbrExposure
{
    Enabled = true,
    Automatic = true,
    MinEv = -6f,
    MaxEv = 6f,
    CompensationEv = 0.5f,
    BrightenSpeed = 2f,
    DarkenSpeed = 4f,
};
scene.DepthOfField = new PbrDepthOfField
{
    Enabled = true,
    FocusDistance = 4f,
    FocalLengthMm = 50f,
    FNumber = 2.8f,
    MaxRadiusPixels = 12f,
};
scene.MotionBlur = new PbrMotionBlur { Enabled = true, ShutterAngle = 180f };
scene.ColorGrading = new PbrColorGrading
{
    Enabled = true,
    Temperature = 0.1f,
    Contrast = 1.05f,
    Saturation = 0.95f,
    Lift = new Vector3(0.01f, 0f, 0f),
    Gamma = Vector3.One,
    Gain = Vector3.One,
    Lut = PbrColorLut.Identity(16),
};
scene.LensDistortion = new PbrLensDistortion { Enabled = true, Strength = 0.03f };
scene.ChromaticAberration = new PbrChromaticAberration { Enabled = true, IntensityPixels = 0.5f };
scene.Vignette = new PbrVignette { Enabled = true, Intensity = 0.2f };
scene.FilmGrain = new PbrFilmGrain { Enabled = true, Intensity = 0.02f, Seed = 17 };
scene.Sharpening = new PbrSharpening { Enabled = true, Strength = 0.2f };
```

Disable a platform's effect with `switches.Set(PbrFeatures.MotionBlur.Id, false)` or configuration:

```toml
[[features]]
name = "rendering.depthOfField"
enabled = false

[[features]]
name = "rendering.filmGrain"
enabled = false
```

Other switch names are `rendering.exposure`, `rendering.motionBlur`, `rendering.colorGrading`,
`rendering.lensDistortion`, `rendering.chromaticAberration`, `rendering.vignette` and
`rendering.sharpening`. `rendering.presentation` controls the final display-effect output pass.
The scene setting and engine switch must both allow the effect.

## Color pipeline and extension contract

| Stage | Setup order | GPU event |
| --- | ---: | ---: |
| Current HDR / optional temporal antialiasing | 720 | 1200 |
| Exposure | 730 | 1210 |
| Depth of field | 740 | 1220 |
| Motion blur | 750 | 1230 |
| Bloom | 800 | 1300 |
| Composite: bloom addition and tonemapping | 900 | 1600 |
| Color grading | 910 | 1610 |
| Lens distortion / chromatic aberration | 920 / 925 | 1620 / 1625 |
| Vignette / film grain / sharpening | 930 / 940 / 950 | 1630 / 1640 / 1650 |
| Optional display antialiasing | 960 | 1660 |
| Presentation | 1000 | 1750 |
| Host overlay | host-defined | 1800 |

`SceneFeature` publishes `PbrResults.SceneColor`, initially `PbrTargets.Hdr`. An HDR effect reads
that result, renders to a distinct texture, and calls
`blackboard.Advance(PbrResults.SceneColor, consumed, produced)`. The guarded advancement refuses
stale consumers or self-feedback; ordinary duplicate `Publish` remains an error. Bloom and
Composite consume the current HDR result. A missing scene result makes the new effects skip.

A display effect requests `FrameRequirements.DisplayColor`. Composite then writes an
`Rgba16Float` **linear tonemapped** intermediate and publishes `PbrResults.DisplayColor`; each
subsequent display effect advances that name. Presentation applies sRGB OETF once, or lets the
sRGB attachment apply it. Overlay runs afterward. With no display effects requested Composite
writes the backbuffer directly, preserving the original pixel baselines and avoiding an extra
pass. HDR grading is not implied: these grading controls deliberately operate after tonemapping.

## Exposure and temporal state

Manual exposure multiplies scene HDR by `2^CompensationEv`, bounded by `MinEv` and `MaxEv`.
Automatic exposure meters the geometric mean of positive Rec.709 luminance, counting the entire
image including sky. A 4×4 reduction carries weighted log luminance and coverage independently,
so odd dimensions do not overweight partial edge blocks. The final GPU pass computes
`log2(MiddleGray / geometricMean) + CompensationEv` and clamps it to the authored EV interval.
The meter and EV history remain on the GPU; no readback or CPU synchronization is required.

Two 1×1 textures alternate exposure history. Adaptation uses
`previous + (target - previous) * (1 - exp(-speed * DeltaSeconds))`, with separate speeds for
brightening and darkening. Delta is limited to 0–1 seconds, rates to 0–100, EV limits to ±24.
The first frame, camera cuts, resize, scene replacement, mode changes, scene disable and engine
switch disable reset adaptation. Call `ExposureFeature.ResetHistory()` through `Pipeline.Find`
to reset explicitly; increment `scene.TemporalHistoryVersion` for a cut affecting all temporal
features. `Tonemap.Exposure` remains a separate multiplicative artistic exposure, applied later
by Composite; use 1 for it when the new exposure controls alone should determine brightness.

## Lenses, motion and color lookup

Depth of field uses a signed thin-lens circle of confusion: foreground radii are negative,
background radii positive, and the focus plane has radius zero. A 64-tap aperture disk gathers
foreground coverage and background blur with a maximum 32-pixel radius. Lens focal length and
sensor height are millimetres; focus and reconstructed view depth are metres. It requires the
opaque depth prepass. Transparent color is filtered using the opaque depth behind it: layered
transparency and hidden background reconstruction are not provided. This is a bounded raster
gather, not a cinematic bokeh scattering solution.

Motion blur uses the camera, rigid-object and skinned-object motion vectors from
`MotionVectorsFeature`. Current-to-previous UV motion is multiplied by `ShutterAngle / 360`,
limited to 64 pixels and sampled with 2–32 trailing shutter taps. Projection jitter is subtracted
from the velocity. Invalid history, background and transparent motion do not blur; foreground
samples closer than `DepthRejectionMetres` are rejected. Motion-vector or depth-prepass switches
off make this pass skip. It cannot reconstruct surfaces revealed by motion or scatter silhouettes
outside their current coverage; rapidly moving thin objects can retain sharp outer edges.

White balance applies temperature/tint in LMS, followed by contrast around linear 0.18,
saturation using Rec.709 luminance, and lift/gamma/gain. A LUT follows these analytic controls.
`PbrColorLut` owns immutable RGB values in [0,1], for a cube with side length 2–64. Supply flattened
pixels in row-major order: `index = green * size * size + blue * size + red`. The GPU texture is
`size² × size` RGBA16Float. Bilinear sampling within two adjacent blue slices and interpolation
between them implements trilinear lookup without bleeding across slice boundaries. The LUT
input clamps to [0,1]; LUT output blends with the analytic result using `LutStrength`. LUT pixels
are **linear** values; decode an sRGB-authored LUT before constructing this class. External LUT
file parsing and color-space metadata import are host responsibilities.

Lens distortion uses normalized radial quadratic/quartic terms; outside the source image is
black, and `Scale` adjusts cropping. Chromatic aberration separates red and blue radially in
pixel units. Vignette blends toward the authored linear color. Grain is deterministic for a
fixed `ElapsedSeconds` and seed, changing at 24 Hz with a luminance-dependent amplitude.
Sharpening uses a five-sample cross, clamped to its local min/max to limit ringing. All passes
run at full resolution; disable or budget them for constrained platforms.

## Validation

`PostProcessingMathTests` covers bounded adaptation, thin-lens signs and aperture scaling,
immutable flattened LUT axes, guarded color chains and reflected uniform/bind-group layouts.
`PostProcessingGpuTests` checks actual pixel changes for every unit, exact baseline restoration
on switch disable, LUT channel permutation and identity transfer, odd-sized automatic metering,
exposure bounds/adaptation/cuts, moving-object blur validity and full-stack ordering/disable.
The existing PBR pass-matrix pixel goldens continue to exercise the defaults with effects off.

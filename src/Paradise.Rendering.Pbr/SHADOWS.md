# Shadows

`ShadowFeature` renders directional cascades, point-light cube faces, and spot-light views into
one bounded `Depth32Float` atlas in `Shadow.Atlas`. The scene and probe GI shade from the same
matrices, tile rectangles, depth ranges, and Poisson PCSS implementation. The array binding stays
compatible with custom material layouts; the physical atlas has one layer. Fog uses the same
atlas rectangles, cascade selection and fade, with unbiased hardware-PCF sampling at volume
points. Contact-shadow sampling lives in the shared raster shading header.

```csharp
var shadows = renderer.Pipeline.Find<ShadowFeature>()!;
shadows.AtlasSize = 4096;
shadows.MapSize = 1024;
shadows.CascadeCount = 4;
shadows.CascadeSplitLambda = 0.65f;
shadows.MaxDistance = 100f;
shadows.CascadeBlend = 0.1f;
shadows.BlurTexels = 16f;

scene.Lights.Add(new PbrLight
{
    Type = PbrLightType.Point,
    Position = new Vector3(0, 3, 0),
    Range = 12,
    Intensity = 10,
    CastsShadows = true,
    SoftShadows = true,
    ShadowSourceRadius = 0.15f,
    ShadowResolution = 512,
    ShadowPriority = 10,
});
scene.ContactShadows = new PbrContactShadows
{
    Enabled = true,
    Length = 0.5f,
    Thickness = 0.05f,
    Steps = 16,
};
```

## Directional cascades

One to four slices use practical PSSM splits: `lambda = 0` gives uniform camera-depth spacing,
`lambda = 1` logarithmic spacing. The default is four slices at 0.65. Each camera-frustum slice
fits a quantized bounding sphere projected into a square, snapped to the light's texel grid.
Growing the scene does not reduce its XY texel density. Light-space depth still covers all
opaque caster bounds, including casters outside the camera slice. Perspective, orthographic,
and infinite-far perspective cameras use the engine's right-handed, zero-to-one depth convention.

`CascadeBlend` reserves an overlapping interval at each boundary and blends visibility through
it. The final cascade fades to unshadowed at `MaxDistance`. The old `DirectionalRadius` property
is a compatibility alias for half `MaxDistance`; nonpositive values use the camera far distance.
`MapSize` is the requested tile size, including its two guard texels.

## Atlas allocation

`AtlasSize` rounds up to a power of two in 512..8192. The default 4096 atlas consumes 64 MiB of
depth storage, independent of light count. Lights are admitted in descending `ShadowPriority`,
then requested resolution, then scene order. All views of one light are allocated atomically:
four directional cascades or six cube faces cannot be partially admitted. Budget pressure halves
all its tiles down to 128, then leaves the light unshadowed if it still cannot fit.

Local lights may author `ShadowResolution`. Zero selects a power-of-two resolution from light
range relative to camera distance, capped by `MapSize`. Identical requests keep their tile
rectangles; removed lights release them on the next frame. New higher-priority allocations can
relocate existing tiles. `ShadowFeature.Views` exposes the most recent allocations and matrices.
Identity follows scene light indices; reordering the scene can cause repacking.

Every tile has a cleared one-texel border. Rasterization uses its inset viewport, and every raw
blocker-search or comparison tap clamps to that viewport's texel centers. This prevents hardware
bilinear comparisons from sampling neighboring lights. The fixed 384-view metadata
budget remains. The shadow draw ring grows geometrically before recording to fit admitted views
multiplied by opaque casters, independently of the main draw ring, and retains its capacity.

## Filtering and contact shadows

`SoftShadows` enables 16 rotated Poisson blocker-search taps followed by 16 comparison taps.
Blocker depths are linearized before averaging. Receiver/blocker separation and emitter radius
set the filter radius, so near-contact shadows harden and separated receivers soften.
`ShadowSourceRadius` is metres for local lights; `ShadowAngularDiameter` is degrees for a
sun (default 0.53). `BlurTexels` limits both search and filtering to 0.5..32 texels. Normal-offset
bias uses each view's actual texel size, and receiver-plane depth correction applies per tap.
The hard path uses one hardware comparison sample. Wide penumbrae are intentionally bounded by
the configured search radius; 16 taps can show spatial noise without temporal accumulation.

Contact shadows march a short world-space ray toward each shadow-casting light through the
visible opaque prepass. They affect direct light only, respect `ShadowStrength`, and fade at the
screen edges and ray-length limit. They cannot see hidden or off-screen blockers. `Thickness`
sets the depth tolerance, so overly large values can falsely occlude separated surfaces; length
and step count trade reach against missed thin blockers. They complement shadow maps and can
also run with shadow maps switched off.

Both `scene.ContactShadows.Enabled` and the `rendering.contactShadows` engine switch must allow
the effect. Disabling `rendering.depthNormalPrepass` retracts contact shadows in the same frame.
The `rendering.shadows` switch controls only the atlas. No persistent contact texture or history
is retained across switches or resizes.

## Validation

`ShadowMathTests` covers practical splits, orthographic/perspective/infinite-far containment,
sub-texel stabilization, allocation pressure, atomic rejection, priority, reuse, and atlas resize.
`ShadowRenderingTests` uses the real WebGPU backend for cascade shadow images at multiple camera
depths, byte-identical atlas relocation/isolation, atlas resize and downscaling, directional/point/spot
emitter-size response, blocker-distance contact hardening,
and contact-shadow feature/prepass retraction. `ShadowLayerTests` keeps nonzero light indices
honest; frame-layout tests validate every new field against Slang reflection.

Capture the GPU comparison images with:

```bash
PARADISE_SHADOW_ARTIFACTS=/tmp/paradise-shadow-review \
  dotnet run --project src/Paradise.Rendering.Pbr.Test -- --treenode-filter '/*/*/Shadow*/*'
```

The full PBR suite also exercises skinned casters, probe GI shading, custom shader bindings, and
the 24-case pass/pixel matrix. The atlas pass, cascade draw count, and filtered shadow pictures
intentionally replace the earlier per-layer baseline.

The frame uniform block grows to 49,600 bytes. Custom game shaders that include the engine lighting
headers must be recompiled with this version; the reflection validator rejects stale layouts.

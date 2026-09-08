# Fog and volumetric lighting

> Sample commands in this document run from [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples), which now owns the sample applications.

Enable fog per scene; the process-wide `rendering.fog` switch can disable it independently:

```csharp
scene.Fog = new PbrFog
{
    Enabled = true,
    Density = 0.02f,
    HeightFalloff = 0.2f,
    BaseHeight = 0f,
    Color = new Vector3(0.2f, 0.25f, 0.3f),
    LightScattering = true,
    Albedo = new Vector3(0.9f),
    Anisotropy = 0.3f,
    MaxDistance = 60f,
    Steps = 48,
};
```

Density is extinction per meter. Height fog density decays exponentially above `BaseHeight`;
`HeightFalloff = 0` produces homogeneous fog. `Color` is constant linear in-scattered radiance.
`Albedo` controls direct-light scattering, and anisotropy selects the Henyey–Greenstein phase:
zero scatters isotropically, positive values favor looking toward a light. Directional, point,
and spot lights use the scene's attenuation and shadow maps. Volume shadows use hardware PCF
at each march point, without surface-normal/depth offsets or the raster surface's wide soft-shadow
filter. `LightScattering = false` retains
height/distance fog with its constant color.

Local media add density inside transformed unit boxes:

```csharp
scene.FogVolumes.Add(new PbrFogVolume
{
    Transform = Matrix4x4.CreateScale(4, 2, 6) * Matrix4x4.CreateTranslation(0, 1, -4),
    Density = 0.1f,
    Albedo = new Vector3(0.7f, 0.85f, 1f),
});
```

The box occupies [-0.5,+0.5] on each local axis before transformation. Overlapping boxes add
extinction and combine their scattering albedos by density. Global density can be zero to draw
local media alone. Up to 16 volumes are supported; non-finite, singular or projective transforms
and excess volumes are rejected. Non-finite color and albedo components become zero; albedo is
clamped to [0,1].

The pass integrates front to back into HDR before temporal AA, bloom, and tone mapping. The
near-plane ray origin is used for orthographic cameras; perspective rays originate at the
camera. Geometry depth stops integration at opaque surfaces; sky rays stop at `MaxDistance`.
`StartDistance` provides a clear region near the viewer. Step count is clamped to 1–128.

This is full-resolution single scattering, with no dedicated temporal accumulation or multiple
scattering. When enabled, TAA accumulates the fogged HDR scene. Cost scales with pixels, steps,
lights and local volumes. Ray–box intersections preserve thin-volume extinction even between
samples. Height density and lighting use midpoint samples; more steps improve rapidly varying
density and shadow detail. Local lighting is approximated at the segment midpoint. Blended
surfaces use opaque depth; their per-layer fog distances are not resolved independently.
The default is disabled and incurs no fog pass or fog GPU resources.

## Run the sample

The Cornell room includes height fog and a local floor volume with `--fog`. Add `--taa` to
accumulate the final HDR scene; `--no-gi` isolates direct volumetric lighting from probe GI:

```sh
dotnet run --project src/Paradise.Rendering.Sample -- --gi-demo --fog --taa --no-gi
```

For reproducible captures, append `--static-lights --size 640x480 --headless 80 --screenshot fog.png`.
Add `--features -rendering.fog` for the matching fog-disabled view. For performance comparisons,
build with `-p:ParadiseProfiling=true`, then run with `--no-build` and append `--bench`.
Compare GPU idle-to-idle frame totals; per-pass timestamps overlap on Apple GPUs.

On an Apple M3 Max, the command above at 640×480 with 48 steps measured 4.18 ms with fog
versus 2.68 ms with the fog switch disabled (60 measured frames after 20 warm-up frames).
These are sample-specific frame totals, not a general performance guarantee.

## Validation

The fifteen `FogTests` cover phase normalization, Beer–Lambert attenuation, height falloff,
opaque depth and infinite-far projection, thin/rotated/overlapping volumes, input validation,
all three light types' shadow maps, coarse shadow-map bias, AA/bloom ordering, resizing and
feature switches. Run them with:

```sh
dotnet run --project src/Paradise.Rendering.Pbr.Test -- --treenode-filter '/*/*/FogTests/*' --maximum-parallel-tests 1
```

Set `PARADISE_FOG_ARTIFACTS` to a directory to save thin-volume and shadowed/unshadowed GPU captures.

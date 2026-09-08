# Fog and volumetric lighting

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
and spot lights use the scene's attenuation and shadow maps. `LightScattering = false` retains
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
local media alone. Up to 16 volumes are supported; singular transforms and excess volumes are
rejected.

The pass integrates front to back into HDR before temporal AA, bloom, and tone mapping. The
near-plane ray origin is used for orthographic cameras; perspective rays originate at the
camera. Geometry depth stops integration at opaque surfaces; sky rays stop at `MaxDistance`.
`StartDistance` provides a clear region near the viewer. Step count is clamped to 1–128.

This is full-resolution single scattering, with no temporal accumulation or multiple scattering.
Its cost scales with pixels, steps, lights, and local volumes. Thin local media can need more
steps. Blended surfaces use the opaque scene depth; their per-layer fog distances are not
resolved independently. The default is disabled and incurs no fog pass or fog GPU resources.

The initial five `FogTests` verify phase normalization, composition of extinction, real GPU
Beer–Lambert attenuation, local volume bounds, direct scattering, and exact restoration when
fog is switched off or density is zero.

# Paradise.Rendering.Sdf

A renderer for scenes described as signed distance fields: a list of analytic shapes combined
with smooth CSG and sphere-traced per pixel. It is a path of its own beside
`Paradise.Rendering.Pbr`, depending only on `Paradise.Rendering`; it draws no meshes and shares
no shaders, targets or scene types with the PBR renderer.

```csharp
using var sdf = new SdfRenderer(backend, switches, width, height);

var scene = new SdfScene
{
    Camera = new SdfCamera
    {
        View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, sdf.AspectRatio, 0.1f, 100f),
    },
};
scene.Shapes.Add(SdfShape.Plane());
scene.Shapes.Add(SdfShape.Sphere(new Vector3(0f, 1f, 0f), 1f) with
{
    Material = new SdfMaterial(new Vector3(0.9f, 0.25f, 0.2f), Roughness: 0.35f),
});
scene.Shapes.Add(SdfShape.Box(Matrix4x4.CreateTranslation(1.3f, 0.6f, 0.4f), new Vector3(0.6f), cornerRadius: 0.08f) with
{
    Smoothness = 0.35f, // melts into the sphere over 35 cm
});
scene.Shapes.Add(SdfShape.Sphere(new Vector3(0.35f, 1.45f, 0.85f), 0.45f) with { Operation = SdfOperation.Subtraction });
scene.Lights.Add(new SdfLight { Direction = new Vector3(0.5f, 0.9f, 0.35f) });

sdf.RenderFrame(scene); // once per frame, on the render thread
```

## The field

`SdfShapeKind` offers sphere, box, capsule, cylinder, torus and plane; `SdfShape.Size` holds each
kind's local dimensions. Shapes fold in list order: each combines with everything before it through
its `Operation` (union, subtraction, intersection), so a subtraction carves the earlier shapes and
none of the later ones. `Smoothness` is the blend radius in world units; zero gives a hard edge. A
union or intersection blends the two materials across that radius, while a subtraction leaves the
carved surface with the material it had.

A transform may rotate, translate and scale uniformly. Shear and non-uniform scale are rejected
with `ArgumentException`: a distance survives only a similarity transform, and anything else makes
the marcher step through surfaces. Stretch a shape through its `Size` instead.

The scene holds at most `SdfRenderer.MaxShapes` (256) shapes and `SdfRenderer.MaxLights` (8)
lights.

## Cost

A pixel costs a few dozen samples of the field whatever it shows: the march to the surface, a
march toward each shadow-casting light, and the taps for the normal and ambient occlusion. What
the scene controls is how much each sample costs:

- Each eye ray and shadow ray tests every shape's bounding sphere once and then samples only the
  shapes it can come near, at most 32; a ray near more than that samples the whole list. Shape
  count therefore costs one bounds test per shape per ray, not one per sample.
- `Smoothness` widens that test. A shape is kept by any ray within 2.5 times the largest blend
  radius from that shape onward in the list, because a blend can carry its influence that far.
  Large blend radii, and hard shapes listed before heavily blended ones, are selected by more rays.
- An intersection is kept by every ray, since it removes whatever lies outside it.
- A union plane joins a ray only where the ray reaches it or passes a shape that could blend
  with it, so open ground costs one step instead of a march down to it. Planes with another
  operation, and planes that blend with each other, are sampled throughout.

Measured at 1920×1080 on a Radeon RX 6900 XT, by replaying the compiled shaders through
wgpu-native's OpenGL backend (Mesa over D3D12) rather than through the engine's Dawn backend.
Direct light and sky ambient only, none of the optional lighting below:

| Scene | ms/frame |
| --- | --- |
| 12 shapes, mostly blended, two shadowed lights | 4.2 |
| 16 shapes on a ground plane, same lights | 3.2 |
| 64 shapes | 4.2 |
| 128 shapes | 5.6 |
| 256 shapes | 8.7 |

Cost is linear in pixels: the first scene takes 2.2 ms at 1280×720 and 14.7 ms at 3840×2160.
Shadows are about half of a frame. Selecting per ray is what keeps the larger scenes affordable;
with every sample walking the list, 256 shapes took 151 ms.

The optional lighting, measured the same way on the sample garden (16 shapes, three shadowed
lights, 4.9 ms without any of it):

| Added | ms/frame added |
| --- | --- |
| Fog, bloom or FXAA | under 0.1 each |
| Traced reflections on surfaces up to roughness 0.5, shadowed | 2.5 |
| The same, unshadowed | 0.7 |
| The same, shadowed, up to roughness 0.2 | 0.5 |
| Probe global illumination: 1,170 probes, 32 rays each | 2.6 |
| The same without visibility weighing | 1.9 |
| All of it | 6.3 |

Of the 2.6 ms for global illumination, 0.7 ms traces and blends the probes and does not grow
with resolution; the rest is the lookup at each pixel. With everything on the garden takes
6.5 ms at 1280×720, 11.2 ms at 1920×1080 and 36.6 ms at 3840×2160.

Code that marches the field costs frame time even in a frame whose settings switch it off: with
the reflection and probe code merely present, the first scene above took 5.4 ms instead of 4.2.
The scene pass is therefore compiled four times, with and without each of the two, and
`SdfSceneFeature` runs the program a frame needs. A frame pays for reflections or probes only
while it uses them.

## Lighting

Directional, point and spot lights use the PBR path's light units (Lambert without 1/π, GGX
specular scaled by π), so an intensity means the same brightness in both renderers. A point or
spot light falls off with the inverse square of distance, windowed to reach zero at `Range`; a
zero range is unbounded. A spot light is at full strength within `InnerConeAngle` of its axis and
fades to nothing at `OuterConeAngle`. `SdfLight.Direction` always points from a lit surface
toward the light, so for a spot it is the reverse of the way the beam points.

Shadows, ambient occlusion and ambient light all read the field itself:

- `SdfTracing.Shadows` marches from the surface toward each light with `CastsShadows`, and
  `ShadowSharpness` sets how hard the penumbra is. The same formula darkens surfaces lit at a
  grazing angle of a few degrees.
- `SdfTracing.AmbientOcclusion` compares the field a few centimetres along the normal with free
  space. It reaches about 13 cm and assumes metre-scale scenes.
- `SdfSky` is a zenith/horizon/ground gradient used as background, ambient light and reflection.
  It is not cosine-convolved.

Everything below is off until the scene enables it.

### Global illumination

`SdfGi` lights the ambient term from a grid of irradiance probes instead of the bare sky:

```csharp
scene.Gi = new SdfGi
{
    Enabled = true,
    BoundsMin = new Vector3(-8f, -0.5f, -8f),
    BoundsMax = new Vector3(8f, 5.5f, 8f),
    ProbeSpacing = 1f,
};
```

Each frame every probe marches `RaysPerProbe` rays through the field. A ray returns the sky, or
the surface it hits lit by the lights, shadowed, plus the light that surface received from last
frame's probes. Each frame therefore adds a bounce, and the probes converge on the sky as the
scene occludes it together with the light surfaces pass on to each other: ground under a roof
darkens, and a lit red wall tints the floor beside it. A surface blends the eight probes around
it.

- Probes hold light as a mean and a first moment, enough for a smooth gradient across the sphere
  and no more. Light varies across a surface no faster than `ProbeSpacing`, so contact shadows
  stay the job of `AmbientOcclusion`, which still applies on top.
- `Hysteresis` is how much of a probe's history survives a frame. The default 0.92 smooths the
  few rays of one frame and takes about a second at 60 frames a second to follow a change in the
  scene or its lights.
- `Visibility` weighs each probe by whether the field is clear halfway to it, so a probe on the
  far side of a wall contributes nothing. Without it light leaks through walls thinner than the
  spacing.
- The grid is centered on the bounds and widens its spacing to stay within `MaxProbes`, at most
  4,096. `SdfGiMath.Fit` returns the grid a setting produces. Changing the bounds, spacing or
  ray count restarts the probes from nothing.
- Outside the bounds the sky's ambient light is used, with a fade over the last cell. A probe
  inside a shape is ignored; a region whose eight probes are all inside shapes falls back to
  the sky.
- A reflection that is not traced reads the probes too, as their light from the mirror direction.

### Reflections

`SdfReflections` traces one mirror ray from each surface no rougher than `MaxRoughness` and shows
what it hits, lit as a matte surface: direct light, shadowed unless `Shadows` is off, plus
ambient light. Roughness blends the mirror image toward the surface's irradiance rather than
blurring it, and a reflection seen in a reflection shows no further reflection. Shadowing the
reflected surface is most of the cost; `MaxRoughness` decides how many pixels pay it.

### Fog

`SdfFog` is exponential fog from the eye to the surface, optionally thinning with height
(`HeightFalloff`, `BaseHeight`) and starting at `StartDistance`. The sky is fogged as if it stood
at `SdfTracing.MaxDistance`, and `MaxOpacity` caps how much fog may hide. It does not shadow
and is not lit by the lights. `SdfMath.FogOpacity` is the same formula on the CPU, for a host
that fogs its own geometry to match.

### Bloom, anti-aliasing and tonemapping

`SdfBloom` adds a glow around pixels brighter than `Threshold`, built from a chain of
progressively halved blurs. `SdfFxaa` smooths edges of the tonemapped frame; it is the only
anti-aliasing, and each pixel is still one ray. `SdfTonemap` selects linear or ACES output with
an exposure.

## CPU queries

`SdfField` packs a list of shapes into the exact data the shader reads and evaluates it on the
CPU with the same formulas, fold order and stepping rule:

```csharp
var field = new SdfField();
field.Set(CollectionsMarshal.AsSpan(scene.Shapes));

var distance = field.Distance(point);              // signed, negative inside
field.Sample(point, out var material);             // blended material at a point
var normal = field.Normal(surfacePoint);

if (SdfMath.TryScreenPointToRay(mouse, viewport, view * projection, out var origin, out var direction, out var length)
    && field.TryRaycast(origin, direction, length, out var hit))
{
    var picked = origin + direction * hit;         // the point the renderer drew under the cursor
}
```

`Common/sdf.slang` and `SdfMath.cs` are twins and must change together. `SdfRendererGpuTests`
renders every primitive and operation and requires each pixel away from a silhouette to match
the CPU field's coverage and blended material. `SdfLightingGpuTests` checks the optional
lighting against values worked out by hand, such as the share of the sky a wall receives
directly and by way of the ground.

## Extending the frame

The frame is a `RenderPipeline` of five features, in this order:

| Feature | Switch | Passes |
| --- | --- | --- |
| `SdfGiFeature` | `rendering.sdfGi` | `Sdf.ProbeTrace`, `Sdf.ProbeBlend` |
| `SdfSceneFeature` | `rendering.sdfScene` | `Sdf.Scene` |
| `SdfBloomFeature` | `rendering.sdfBloom` | `Sdf.Bloom.Bright`, `.Down`, `.Up` |
| `SdfCompositeFeature` | `rendering.sdfComposite` | `Sdf.Composite` |
| `SdfFxaaFeature` | `rendering.sdfFxaa` | `Sdf.Fxaa` |

A switch turns a feature off whatever the scene asks; with the switch on, global illumination,
bloom and FXAA run only while the scene's settings enable them. A host adds its own feature
through `renderer.Pipeline.Add(feature, order)` at a slot between those in `SdfFeatureOrder`:

- `SdfResults.SceneColor` is the current linear HDR stage on the frame blackboard. Read it, and
  advance it if the feature writes a new stage.
- `SdfResults.Probes` is the probe atlas of a frame in which global illumination ran, and
  `SdfResults.Bloom` the glow the composite will add.
- A feature that returns `FrameRequirements.DisplayColor` makes the composite tonemap into
  `SdfTargets.DisplayColor` instead of the backbuffer and publish it as
  `SdfResults.DisplayColor`, linear and unencoded. That feature must then present the frame,
  as `SdfFxaaFeature` does.
- `SdfTargets.Depth` holds the device depth of the traced surface, and 1 for sky. A raster pass
  that loads it as its depth attachment draws gizmos, particles or meshes correctly in front of
  and behind the traced shapes. `SdfHostFeatureTests` shows such a pass.

Upload and release host resources on the render thread between `RenderFrame` calls. A rejected
scene throws before anything is submitted and leaves the renderer usable.

## Sample

`Paradise.Rendering.Sdf.Sample` in [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples)
opens a window on an animated scene with an orbit camera, click-to-pick and a key to toggle
each of the lighting features above. Like every sample
there it builds against the published package, so it runs once a release includes this one.

## Tests

```bash
dotnet test --project src/Rendering/Paradise.Rendering.Sdf.Test/Paradise.Rendering.Sdf.Test.csproj --output normal
```

Distance functions, the field, the probe grid and ray set, the fog formula and the uniform
layouts are tested without a GPU. Tests that render
skip when no WebGPU adapter is available and run one at a time: a software adapter traces every
pixel on CPU threads, and concurrent frames outlast the backend's five-second readback wait.
CI runs this project in its GPU phase (`.github/scripts/run-tests.sh gpu`), where a missing adapter
fails instead of skipping.

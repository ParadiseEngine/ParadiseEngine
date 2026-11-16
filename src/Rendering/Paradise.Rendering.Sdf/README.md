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
wgpu-native's OpenGL backend (Mesa over D3D12) rather than through the engine's Dawn backend:

| Scene | ms/frame |
| --- | --- |
| Sample garden: 12 shapes, mostly blended, two shadowed lights | 4.2 |
| 16 shapes on a ground plane, same lights | 3.2 |
| 64 shapes | 4.2 |
| 128 shapes | 5.6 |
| 256 shapes | 8.7 |

Cost is linear in pixels: the garden takes 2.2 ms at 1280×720 and 14.7 ms at 3840×2160. Shadows
are about half of a frame. Selecting per ray is what keeps the larger scenes affordable; with
every sample walking the list, 256 shapes took 151 ms.

## Lighting

Directional and point lights use the PBR path's light units (Lambert without 1/π, GGX specular
scaled by π), so an intensity means the same brightness in both renderers. A point light falls off
with the inverse square of distance, windowed to reach zero at `Range`; a zero range is unbounded.

Shadows, ambient occlusion and ambient light all read the field itself:

- `SdfTracing.Shadows` marches from the surface toward each light with `CastsShadows`, and
  `ShadowSharpness` sets how hard the penumbra is. The same formula darkens surfaces lit at a
  grazing angle of a few degrees.
- `SdfTracing.AmbientOcclusion` compares the field a few centimetres along the normal with free
  space. It reaches about 13 cm and assumes metre-scale scenes.
- `SdfSky` is a zenith/horizon/ground gradient used as background, ambient light and reflection.
  It is not cosine-convolved, and reflections never show other shapes.

`SdfTonemap` selects linear or ACES output with an exposure. There is no anti-aliasing, bloom or
other post-processing in this path yet.

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
the CPU field's coverage and blended material.

## Extending the frame

The frame is a `RenderPipeline` of two features, `SdfSceneFeature` and `SdfCompositeFeature`,
with the switches `rendering.sdfScene` and `rendering.sdfComposite`. A host adds its own through
`renderer.Pipeline.Add(feature, order)` at a slot between `SdfFeatureOrder.Scene` and
`SdfFeatureOrder.Composite`:

- `SdfResults.SceneColor` is the current linear HDR stage on the frame blackboard. Read it, and
  advance it if the feature writes a new stage.
- `SdfTargets.Depth` holds the device depth of the traced surface, and 1 for sky. A raster pass
  that loads it as its depth attachment draws gizmos, particles or meshes correctly in front of
  and behind the traced shapes. `SdfHostFeatureTests` shows such a pass.

Upload and release host resources on the render thread between `RenderFrame` calls. A rejected
scene throws before anything is submitted and leaves the renderer usable.

## Sample

`Paradise.Rendering.Sdf.Sample` in [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples)
opens a window on an animated scene with an orbit camera and click-to-pick. Like every sample
there it builds against the published package, so it runs once a release includes this one.

## Tests

```bash
dotnet test --project src/Rendering/Paradise.Rendering.Sdf.Test/Paradise.Rendering.Sdf.Test.csproj --output normal
```

Distance functions, the field and the uniform layouts are tested without a GPU. Tests that render
skip when no WebGPU adapter is available and run one at a time: a software adapter traces every
pixel on CPU threads, and concurrent frames outlast the backend's five-second readback wait.
CI runs this project in its GPU phase (`.github/scripts/run-tests.sh gpu`), where a missing adapter
fails instead of skipping.

# Motion vectors

`MotionVectorsFeature` produces motion from the previous rendered frame to the current frame.
It tracks camera transforms, each `PbrInstance`'s model transform, and the previous and current
GPU skin palettes. Palette slots can move between frames: history retains the old offset and
matrix data independently of the current palette.

The engine switch is `rendering.motionVectors` (enabled by default). The pass and its GPU
resources are created only when the scene opts in or an enabled feature requests motion:

```csharp
scene.MotionVectors = new PbrMotionVectors { Enabled = true };
var motion = renderer.Pipeline.Find<MotionVectorsFeature>()!;
```

A temporal feature requests `FrameRequirements.MotionVectors` from its `Requires` property,
sets up after `PbrFeatureOrder.MotionVectors`, and reads `PbrResults.MotionVectors` from the
frame blackboard. The switch can still disable the producer; a missing result requires the
consumer to use its current frame without temporal history. `motion.View` exposes the same
target for external consumers and becomes invalid when the producer is inactive or resized.
The result is exported, so its pass and stored pixels survive graph culling even without a
consumer in the graph. The pass appears as `MotionVectors.Geometry` in `LastPassNames`.

## Texture contract

`PbrTargets.MotionVectors` is a full-resolution `Rgba16Float` texture:

| Channel | Meaning |
| --- | --- |
| XY | `currentUv - previousUv`, in normalized UV units, with the origin at the top left |
| Z | Previous device depth of this surface point, in WebGPU's `[0, 1]` range |
| W | `1` when previous geometry is available; `0` when history must be rejected |

The projection matrices used to render each frame also produce its motion. Projection jitter
is included: sample saved color at `currentUv - motion.xy` without subtracting jitter again.
Compare saved depth at that UV with `motion.z` to detect disocclusion; also reject UVs outside
the history image. `motion.w` alone does not prove that this point was visible last frame.
Clip positions are interpolated before perspective division, so this convention also holds
on slanted triangles and during perspective camera or object motion.

The motion pass depth-tests the built-in geometry independently of the lighting prepass.
Opaque geometry writes motion; background pixels remain zero. Visible blended or transmissive
triangles overwrite motion with zero to reject history, including where an opaque object lies
behind them. This is conservative across the entire triangle and does not sample material
alpha. Transparent geometry behind opaque surfaces leaves the opaque motion intact.

## Resetting history

History starts invalid and resets automatically after a resolution change, a different
`PbrScene` object, an inactive frame, or a switch transition. Keep each `PbrInstance` object
alive across frames: history uses its reference identity, so sorting the list does not change
its history, while replacing or removing an instance invalidates its old state. A newly
added instance begins without history even if it shares another instance's mesh.

For a camera cut, teleport, or discontinuous geometry edit, increment the scene's
`TemporalHistoryVersion` before rendering:

```csharp
scene.Camera = nextCamera;
scene.TemporalHistoryVersion++;
```

`motion.ResetHistory()` also resets this producer on its next frame. Call it on the render
thread between frames. `motion.HistoryReady` reports whether the current frame has a preceding
rendered frame to reference; individual pixels still require their W validity check. Consumers
with their own saved color/depth should discard those histories on the same cuts and resets.

GPU skinning is covered, including unchanged poses and palette updates made while the motion
feature was inactive. CPU deformation through `UpdatePrimitiveVertices` and custom vertex
displacement are not covered: the built-in pass only has the current uploaded vertex stream,
plus the built-in GPU skinning data. Those surfaces need a matching motion pass with their
previous deformation, or temporal history must be rejected for them. A global reset is
suitable for a discontinuous edit, but would prevent accumulation if repeated every frame.

`MotionVectorsTests` reads the actual GPU result through a diagnostic render pass and checks
object/camera motion, jitter, perspective reprojection, previous depth, skin deformation and
palette relocation, identity/visibility changes, transparent rejection, and reset paths.

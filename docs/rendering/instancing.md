# Instancing and batching

The built-in PBR renderer automatically batches consecutive draws that share geometry, material,
and vertex path. Rigid and skinned instances retain their own model, normal transform, highlight,
GI mode, and skin palette offset. Adjacent transparent draws can batch while retaining their
back-to-front order. Custom material programs use their registered vertex entry points and remain
individual draws.

The process switch is `rendering.instancing` (enabled by default). A scene can disable it with:

```csharp
scene.Instancing = new PbrInstancing { Enabled = false };
```

Inspect the feature after rendering:

```csharp
var instancing = renderer.Pipeline.Find<InstancingFeature>()!;
Console.WriteLine($"{instancing.DrawCalls} scene draws; {instancing.SavedDrawCalls} saved");
```

`BatchedInstances` counts the primitive instances emitted in multi-instance draws. Statistics refer to the
main opaque/transparent scene passes. Shadow and depth prepasses still issue individual draws;
they retain the same uniform-ring indices as the scene. The existing 4096-draw staging limit
counts original primitives, including those that batch.

Instancing resources and pipeline variants are created only when a batch actually needs them.
Per-instance data is uploaded once before submission, with the shader's 208-byte storage stride.
Disabling the feature restores individual draws at the next frame boundary.

The renderer does not reorder opaque submissions across different materials to form larger
batches: doing so would change the winner for coplanar surfaces. Place repeated instances next
to each other to batch them. Geometry uploads are shared through `PbrMesh`; instancing does not
merge different meshes into one vertex buffer.

Frustum-culled draws split batches while retaining every original uniform slot. When GPU
occlusion is active, opaque draws use their individual indirect arguments; transparent draws
can still batch. `DrawCalls` counts submitted commands, including indirect commands whose GPU
instance count may be zero.

Validation covers rigid and skinned instances, nonuniform scale, per-instance highlight and GI
mode, transparent order, nonzero first-instance offsets, runtime switches, and frustum gaps
with GPU occlusion both on and off. It checks actual submitted commands and byte-identical
pixels against individual draws. The visibility suite also compares eight moving-camera TAA
frames with fog and directional shadows against unculled rendering.

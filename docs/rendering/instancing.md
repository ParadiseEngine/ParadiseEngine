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

Validation: five `InstancingTests` run on WebGPU. Four prove that six objects become one
scene draw with byte-identical output to instancing disabled, including skin palette offsets,
nonuniform scale, transparency, nonzero first-instance offsets, and runtime changes. The fifth
checks that disabling the scene clears statistics even when instancing is already off. The full
80-test PBR suite, including the pass/pixel baselines, passes on macOS arm64 with no skipped tests.

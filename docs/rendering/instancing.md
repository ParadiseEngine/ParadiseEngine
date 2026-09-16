# Instancing and batching

The PBR renderer automatically batches consecutive compatible draws. Main-pass compatibility
includes the complete primitive descriptor (including geometry buffers, ranges and material) and
effective skinning mode. Rigid and skinned instances retain their own model, normal transform,
highlight, GI mode, decal flag and skin palette offset. Adjacent transparent draws can batch while
retaining their back-to-front order. Custom material programs can opt into instancing.

The process switch is `rendering.instancing` (enabled by default). A scene can disable main,
depth/normal and shadow batching with:

```csharp
scene.Instancing = new PbrInstancing { Enabled = false };
```

Both controls take effect at the next frame boundary. They do not disable visibility culling.

## Optional packing and regrouping

`PackAndRegroup` defaults to false. Main-pass batching preserves submission order, writes draw
uniforms directly into the aligned uniform ring and stages extra main-pass instance data only
when an actual batch needs it. A scene can opt into both canonical packed main-draw storage and
grouping of nonconsecutive eligible opaque draws:

```csharp
scene.Instancing = new PbrInstancing { PackAndRegroup = true };
```

The combined option requires both scene instancing and the process switch to be enabled. Its
extra packing and grouping work can cost more than the saved draw encoding, so measure the
target scene before enabling it. Regrouping can change which surface wins at equal depth.
Grouping preserves the order of first appearance of each batch and the instance order within a
batch. Alpha-masked materials and
custom programs without explicit reordering permission are boundaries that grouping never crosses.
Transparent draws retain back-to-front order.

Disabling `PackAndRegroup` retains camera and shadow culling, consecutive main-pass batching,
custom-material instancing, and depth/normal and shadow batching. It controls the main-draw
packing and regrouping strategy, independently of those capabilities.

Sharing a material or looking alike does not make different geometry buffers compatible. Reuse
uploaded `PbrMesh` geometry; instancing does not merge separate uploads into a common vertex buffer.

## Custom materials

Register a custom rigid program with `MaterialProgramOptions`. Each capability is independent:

| Option | Contract |
| --- | --- |
| `PreservesMeshBounds` | The vertex shader remains inside the uploaded local bounds, permitting camera culling for rigid, static primitives. |
| `AllowsOpaqueReordering` | Opaque materials using the program may regroup when the scene opts in. |
| `OpaqueCoverage` | The shader covers stock rigid geometry without displacement or fragment discard, permitting use as an occluder. |
| `InstancedVertexEntryPoint` | The named rigid vertex entry reads the packed per-instance draw data. |
| `InstancedFragmentEntryPoint` | An optional fragment entry reads per-instance data instead of the ordinary single-draw uniform. |

Unspecified capabilities remain disabled for custom programs. A dissolving roof can preserve mesh
bounds and support instancing without promising solid coverage or allowing reordering. Masked,
blended and transmissive materials are excluded from solid occlusion independently of the program's
coverage declaration. Custom material programs remain rigid-only.

Include `Common/pbrInstancing.slang` and implement the instanced vertex entry with
`pbrTransformVertexInstanced(input, instanceId)`, where `instanceId` is `SV_InstanceID`. An ordinary
fragment entry can be reused when it does not read the single-draw `draw` uniform. Varyings already
carry the per-instance highlight and rendering flags.

For a fragment effect that reads the model matrix, provide a separate instanced fragment entry
accepting `InstancedFragmentInput`. Its `surface` member is the ordinary fragment input, and
`pbrInstanceDraw(input.instanceDrawIndex)` supplies the correct model and flags. The instance index
includes the draw's `FirstInstance`, so it need not start at zero. Material bindings remain part of
main-pass batch compatibility; different room dissolve buffers require distinct material IDs.

## Depth and shadows

The depth/normal prepass batches consecutive compatible visible geometry in the main pass's final
order, ignoring material differences. It does not independently regroup geometry, preserving
equal-depth normal winners. Its instanced records are compacted into a separate storage buffer;
they copy the captured object values directly and do not depend on main-draw packing. The
noninstanced path continues to use the main uniform-ring slots.

The shadow atlas groups compatible geometry across materials independently for each light view.
Shadow rendering uses the common caster shader, so custom fragment discard remains absent from
the shadow pass. Both auxiliary paths retain full rigid/skinned vertex strides, per-instance
transforms and individual joint-palette offsets. A skinned stream without an assigned palette
renders in bind pose using its full stream stride.

Shadow caster culling tests the transformed primitive bounds against each light view, independently
of camera visibility. Skinned, dynamic and unknown bounds remain submitted, so a camera-invisible
caster can still cast a visible shadow. `ShadowFeature.CasterCullingEnabled` controls this culling
separately from instancing and camera-frustum culling.

## Frame data and visibility

`RenderFrame` captures transforms and rendering flags after `PrepareFrame` callbacks and before
feature setup. Finish transform and palette changes before that boundary. The renderer computes
object transforms once and retains the captured objects for all passes, including motion history.
With `PackAndRegroup=false`, primitive values are written directly to the uniform ring. Main-pass
instance staging is allocated and filled only if compatible visible draws form an actual batch.
With `PackAndRegroup=true`, a canonical packed array is also populated in final raster order.
These paths share one `Frame.Draws` array rather than maintaining duplicate CPU instance arrays.
Once allocated, its capacity is retained; a later default-path frame without a main batch does
not refresh its data.
Trace geometry is built before opaque regrouping so its submission order and hierarchy-sharing
opportunities remain unchanged.

Main-pass instance storage uses the existing 208-byte draw layout and uploads the chosen path's
instance data when needed. The aligned uniform ring remains available for ordinary draws. The
instanced prepass uploads its own 208-byte records; shadows upload compact 80-byte light-MVP and palette
records per admitted caster/view. These paths reduce repeated transform work and draw encoding;
they do not eliminate all duplicate uploads or introduce a new compact main shader layout.

Draw and instance buffers grow geometrically and retain capacity between frames. The initial
4096-record allocation is not a fixed draw limit; capacity remains subject to allocation and device
buffer limits. Main-pass instance resources and pipeline variants are created lazily, while enabled
auxiliary passes create their instance resources when recorded.

Frustum-culled main draws keep their uniform slots and split main-pass batches. When GPU occlusion
is active, opaque main draws use individual indirect arguments; transparent draws can still batch.
Main uniform indices and indirect arguments refer to final raster order, while the instanced
prepass and shadow atlas use their separate compact storage indices.

## Diagnostics and validation

Inspect the features after rendering:

```csharp
var instancing = renderer.Pipeline.Find<InstancingFeature>()!;
Console.WriteLine($"{instancing.DrawCalls} scene draws; {instancing.SavedDrawCalls} saved");
var prepass = renderer.Pipeline.Find<PrepassFeature>()!;
var shadows = renderer.Pipeline.Find<ShadowFeature>()!;
Console.WriteLine($"{prepass.DrawCalls} prepass draws; {prepass.SavedDrawCalls} saved");
Console.WriteLine($"{shadows.DrawCalls} shadow draws; {shadows.SavedDrawCalls} saved; " +
    $"{shadows.CulledDrawCount} casters culled across light views");
```

`InstancingFeature.BatchedInstances` counts primitive instances emitted in multi-instance main
draws. Its other statistics also cover only the opaque/transparent scene passes. `DrawCalls` counts
submitted commands, including indirect commands whose GPU instance count may be zero. Auxiliary
features report their own draw savings, and shadow culling counts rejected caster/view pairs.

Validation compares actual submitted commands and byte-identical images against individual draws.
It covers rigid/skinned and custom materials, nonuniform transforms, distinct material bindings,
nonzero instance offsets, transparency, ordering boundaries, buffer growth, visibility gaps, light
frustum culling, and runtime switches. Motion tests cover unposed skinned streams and palette
transitions; existing pass-matrix image baselines remain unchanged by auxiliary batching.

See [Neon-city profiling](neon-city-profiling.md) for scene measurements and their limitations.

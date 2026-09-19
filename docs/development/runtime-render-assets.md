# Runtime render assets

## Boundary

GLB/glTF is an import/build format, not a runtime renderer input. Importers extract source
containers and cook `.mesh` / `.skinnedmesh` to MeshBlob, `.skeleton` / `.anim` to ozz archives,
and textures to KTX2. Runtime loaders read those outputs and resolve baked material and prefab
references. `Paradise.Rendering.Pbr` has no dependency on `Paradise.Assets.Gltf`.

The loader owns asset identity, I/O, dependencies, sharing, upload transactions and unload policy.
The renderer owns GPU resources and trace geometry. Do not reconstruct a source container or add
a source-format adapter to the runtime renderer merely to upload cooked data.

## Materials and textures

Map a cooked material document to `PbrMaterialDesc`, then supply each independently resolved
texture payload through `PbrMaterialTextures`. There is no per-container image table or image
index. Empty memory selects the corresponding default texture. For example, after the loader
reads `baseColorKtx2` and `normalKtx2` from the cooked paths:

```csharp
var description = new PbrMaterialDesc
{
    Name = "painted-metal",
    BaseColorFactor = tint,
    MetallicFactor = 0.8f,
    RoughnessFactor = 0.35f,
};
var textures = new PbrMaterialTextures
{
    BaseColor = baseColorKtx2,
    Normal = normalKtx2,
};
var materialId = pbr.Materials.AddMaterial(description, textures);
```

`AddMaterial` consumes memory synchronously without retaining it. Keep payloads valid and
unchanged until the call returns; afterwards the loader may release the source memory.
The cache shares uploads by content and usage: base color/emissive are sRGB, metallic-roughness
and occlusion are linear, and normal maps use the normal-map transcode path. Destroying one
material does not destroy a texture still used by another. Malformed payloads retain the
existing fallback behavior. A failed upload releases its partial resources and acquired shares.

Custom programs, extra bindings and target-following entries use the same `AddMaterial` method:
`programId`, `extraEntries` and `targets` are optional named arguments. Extra GPU resources remain
caller-owned. PBR alpha modes preserve blending and conservative coverage classification;
`Mask` does not add stock shader discard. Custom material programs implement cutout behavior.
The stock pipelines remain double-sided. No previously unused source alpha-cutoff or
source double-sided fields are carried into the runtime description.

## Cooked geometry

`UploadPrimitive` accepts `ReadOnlySpan<float>` and `ReadOnlySpan<uint>`. The rigid stream is
12 floats per vertex. `UploadSkinnedPrimitive(vertices, indices, materialId)` accepts a cooked
20-float interleave; the overload with a separate joint/weight span remains useful for
procedural data. Every overload consumes its spans synchronously and retains no source memory.

A loader can upload a cooked draw without constructing managed geometry arrays:

```csharp
using var reference = MeshBlobFormat.Open(cookedBytes);
ref var mesh = ref reference.Value;
var vertices = mesh.Vertices.ToSpan();
var indices = mesh.Indices.ToSpan();
var uploaded = new List<PbrPrimitive>();
try
{
    for (var i = 0; i < mesh.Draws.Length; i++)
    {
        ref var draw = ref mesh.Draws[i];
        var drawIndices = indices.Slice((int)draw.FirstIndex, (int)draw.IndexCount);
        var materialId = materialIds[draw.MaterialSlot];
        uploaded.Add(draw.IsSkinned
            ? pbr.UploadSkinnedPrimitive(vertices, drawIndices, materialId)
            : pbr.UploadPrimitive(vertices, drawIndices, materialId, stride: mesh.FloatsPerVertex));
    }
}
catch
{
    foreach (var primitive in uploaded) pbr.ReleasePrimitive(primitive);
    throw;
}
```

`materialIds` is the loader's resolved slot map; validate it before allocating and retain every
material ID the loader creates, including materials not currently referenced by a draw. The
example borrows those materials, so geometry rollback does not release them. A loader creating
materials in the same transaction must also roll back its owned material IDs. Retain the
skeleton, skin bindings and draw/node metadata required for animation independently of GPU
geometry, and stage the evaluated joint palettes before rendering. Do not copy relative-pointer
MeshBlob headers: access blob members through `ref` while their owning reference is alive.

This example uploads each draw independently, including its vertex stream. It is not a packed
shared-buffer mesh uploader. Such packing is a separate optimization, not an import boundary.

## Migration and lifetime

Both source-container `PbrRenderer.UploadMesh` overloads are removed, with no obsolete forwarding
shim. The old glTF-typed `AddMaterial` overloads are also removed. Engine-owned `PbrMaterialDesc`,
`PbrAlphaMode`, `PbrUvTransform` and direct KTX2 inputs replace their runtime usage. Sprites use the
same material path. `Paradise.Assets.Gltf` remains available to import/build tools and their tests.

Before adopting this engine version, downstream sample/viewer and game loaders must replace
source-scene loading with cooked assets and migrate material construction to the new inputs.
A dedicated source preview tool may import and cook on demand before using the runtime path;
it must not restore glTF dependencies to the renderer or game runtime. Downstream repositories
have their own commits and package pins; this engine change does not update them automatically.

Unload on the render thread between frames, after all raster/GI users retire and recorded work
has been submitted or discarded. Release each owned primitive once, then owned material IDs,
then unused custom programs and caller-owned extra resources. Descriptor copies borrow the
same geometry lifetime and do not acquire another lease. Asset-level sharing is the loader's
responsibility; renderer disposal is a final cleanup, not the normal asset-unload mechanism.

Material release retires the ID before backend destruction and attempts its bind group, uniform
buffer and every acquired texture share even when a destroy throws. Shared textures remain live
for other materials. Cache disposal closes the cache first, then attempts every live material,
both defaults and the sampler. Repeated release/disposal does not retry a handle that a throwing
backend may already have invalidated. PBR renderer disposal still attempts its other owning
subsystems after a material-cleanup failure.

A single cleanup failure is rethrown with its original identity and stack; multiple failures are
reported together as an `AggregateException`. Upload rollback preserves the triggering error,
combining it with cleanup errors when necessary. These are best-effort release guarantees, not a
claim that a throwing backend freed native storage; the host must still dispose its backend/device.

`RuntimeAssetBoundaryTests` checks the dependency boundary, real cooked rigid/skinned uploads,
source-memory independence, material slot mapping, unload and failed multi-draw rollback.
Material lifecycle tests cover texture sharing, failed uploads and target/extra-binding updates.

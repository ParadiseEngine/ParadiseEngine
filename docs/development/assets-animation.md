# Asset pipeline and animation

Runtime renderers consume cooked geometry spans, engine-owned material descriptions and standalone
KTX2 inputs; source-container import stays in build tooling. See [runtime render assets](runtime-render-assets.md)
for the API migration, ownership boundary and cooked upload examples.

### Identity and outputs

`AssetReference` is `{ guid, path }`: **GUID identifies; path is a readable hint**.
Pass `AssetReference` across reference APIs, not a raw path.
Share one `AssetIndex` scan across build, bake, resolve, verify and repair. Resolve with
`AssetIndex.AssetOf`, never `assetsRoot / reference.Path`; key caches and cycle checks by GUID.
Stale paths after external renames are warnings, repairable with `verify --fix`; missing GUIDs are
errors. `assets mv` updates hints eagerly while retaining sidecar identity.

A model source (`.glb`, `.gltf`, or a format in `BlenderModelConverter.Extensions`) is source only and builds no output. Tool-owned
`.mesh`, `.skinnedmesh`, `.skeleton` and `.anim` documents name its parts with `{ source, asset,
slot, name, index, hash, skeleton }` (`asset` only for one model of a `.blend` holding several). Prefabs reference those documents, not the model. Meshes cook to
aligned native MeshBlob data (magic/version first); skeletons and clips cook to ozz archives. Clip
lookup uses name, then content hash, then index.

The GLB determines rigid versus skinned kind. A skinned document names its `.skeleton`; MeshBlob
v3 stores that skeleton's **built path**. Kind mismatches are build errors; when the GLB gains or
loses its rig, replace the stale document with a fresh identity.

Every GLB read of a model source goes through `ModelSource.ReadGlb`. A `.gltf` is a direct source
like a `.glb`: `GltfFile` concatenates its buffers (relative files read through `fileSystem`, so a
build records a `.bin` as an input, or `data:` uris) four-byte aligned into one BIN chunk,
re-offsets the buffer views and moves `data:` images into buffer views, so extraction treats them
as embedded; image uris stay relative to the `.gltf`. Buffer uris follow the image rule
(`MeshContainer.AssetPathFor`: percent-decoded, relative, confined to `assets/`), and each buffer
view must lie within its own buffer with a glTF-legal `byteStride`, or the read is an
`InvalidDataException` naming the view. A `.gltf`'s buffer files are container references like its
images: `MeshContainer` reads and rewrites `buffers[N]` slots beside `images[N]` (a `data:` buffer
names no file), so the sidecar records a `.bin` by identity and `assets mv`, `rm`, `refs` and
`verify --fix` follow and repair it as they do a texture. Writes go through
`ModelSource.WriteGlb`, which puts a `.gltf` back as indented JSON plus its one buffer (a `.bin`
rewritten only when its bytes change, a `data:` buffer kept inline) and refuses a `.gltf` with
several buffers; `MeshContainer` reads and rewrites a `.gltf`'s JSON directly. The Blender addon
normalizes and writes the same shape. A converted source (every
extension in `BlenderModelConverter`'s import table, from which `ModelSource.IsConverted`,
`GlbImporter` and `assets convert` derive) is read through `fileSystem` (so a build records it as
the input), then through its converted GLB at `.editor/converted/<assets-relative source>.glb`,
written on the host because a build's observed file system is read-only. The conversion script
dispatches on extension, exports the GLB and lists the external files the import read, also when
the import or export fails;
`BlenderModelConverter` stamps `asset.extras` with `paradiseSourceSha256`, `paradiseConverterVersion`,
`paradiseBlenderVersion` and `paradiseDependencies` (`[{ path, sha256 }]`, paths relative to the
source's directory with `/`), a contract shared with the Blender addon. Bump `ConverterVersion`
whenever the script or export settings change. Reuse needs a matching source hash, converter version
and every dependency hash, plus a matching Blender version unless no Blender is found; each
dependency is hashed through `fileSystem` on every read, so a build records it and a changed texture
or `.mtl` rebuilds; one `fileSystem` cannot reach (another drive, or above a project-rooted mount)
is hashed on the host, so it is stamped and checked but is not a build input. The stamp's Blender
version is not a file either, so `BuildRunner` adds `converter=` and `blender=` (the
`blender --version` line, empty without Blender) to the index environment when the project has a
converted source, and only then runs Blender to ask. A stale GLB without Blender is an
`InvalidDataException` naming `PARADISE_BLENDER_PATH`; the script needs Blender 4.4 or newer
(`BlenderModelConverter.MinimumBlenderVersion`: `bpy.data.file_path_map` arrived in 4.4) and exits
naming the found and required versions. Each conversion notes its start time; when the source or a
listed dependency was written or removed since, or the source's hash changed, the result is
discarded and converted again, up to three attempts. The persisted GLB is the only cache of a
successful conversion; the process remembers per source only its latest failure (and, for a memory
mount, which converts in a temporary directory and persists nothing, its GLB). A remembered failure
stands while the source hash, the Blender version and the hash of every file the failed run listed
are unchanged, so fixing a `.mtl` under a running watch converts again; a run that failed before
listing anything (a `.blend` Blender could not open, a crash) is keyed on source and Blender alone.
`AssetMover` moves `.editor/converted/` entries with their sources (the whole-source GLB and the
per-asset directory), and deletes those of a source that changed extension.
Converted sources are read-only: `MeshContainer` neither reads nor rewrites them, extracted images
bind materials through the extraction record, and a document-side material edit is recorded rather
than written back. An embedded image whose SHA-256 equals a stamped dependency under `assets/` (the
exporter embeds an unmodified external texture as its file's bytes) binds that file instead of being
extracted, and is not recorded. A GLB with skins or animations but no drawable mesh extracts
`.skeleton` and `.anim` documents only: no mesh document and no prefab seed.

#### Several models in one `.blend`

A `.blend` with collections whose `asset_data` is set is one model per such collection (local, not
linked), named by the collection; objects in no asset collection are not exported, and a `.blend`
with none is one whole-file model. The conversion script rejects a name that is not a file name on
every platform or that collides ignoring case, and an asset collection nested anywhere under another
(its objects would be in both models), naming both. It exports each collection alone (active
collection with nested, active scene; its layer and every ancestor layer un-excluded) with its
roots moved by `-instance_offset` through `delta_location`, so each asset's origin is its
collection's instance offset and an animation of location is unaffected.
`Convert` returns the whole-source GLB or one GLB per asset, never both; `ModelSource` persists them
at `.editor/converted/<rel>.glb` or `.editor/converted/<rel>/<asset>.glb`, deleting the other form
and assets no longer exported. Each per-asset GLB is stamped as above plus `paradiseAsset` (its
name, checked by `IsCurrent`) and `paradiseAssets` (every asset). One current GLB names the assets,
but `ModelSource.Assets` answers from the stored conversion only while every listed asset's GLB is
current (the writes are not atomic as a set, and a GLB may be deleted); otherwise it converts again.
`ConverterVersion` is 3. `ModelSource.ReadGlb(..., asset)` reads one asset; the whole of a file
with assets, an unknown asset, or an asset of a source that cannot hold them is an
`InvalidDataException` (`ModelSource.AssetProblem`).

The `.blend` has one sidecar. `ExtractedPart.Asset` (`asset` in each `[extract]` part) names the
model a part is of; `GlbImportSettings.ReadExtraction(meta, asset)` and `WriteExtraction` work on
one model's parts and keep the others', `ReadExtractions`/`WriteExtractions` on all. Model
documents carry an optional top-level `asset` beside `source` (`MeshReferenceDocument.Asset`), which
cooks, verify, scene geometry and navigation baking pass to `ReadGlb`; references, `mv` and `rm` are
unchanged since the edge is still the `.blend`'s GUID. `AssetExtractor` runs once per asset with the
asset as the stem of every file it names and the manifest's routes; afterwards it drops from the
record the parts of models the source no longer holds (a renamed or removed collection, or the
whole-file model of a file that gained collections) and warns naming their files, which stay under
their identities. `ProjectVerifier` reports a document whose asset the source does not hold. The
Blender addon's per-clip settings, `[glb] clips = [{ asset?, index, name, root_motion?, root_bone? }]`,
are kept verbatim by `GlbImportSettings` (an entry with a `guid` is the legacy extraction record).

#### Replacing GLBs with `.blend` sources

`BlendMigration` (`paradise assets to-blend <glb...|dir> [--families] [--dry-run]`) groups `.glb`
sources by stem, or with families by directory and the stem without a trailing `_<8-12 hex>`. One
Blender run builds every staged `.blend` (`.<name>.blend.paradise-staging` beside its GLBs, so
relative texture paths hold): each GLB imported with `import_pack_images=False`, a family member
into an asset collection of its own, moved along X by an offset that is also its `instance_offset`,
materials that every member defines alike merged. Each staged file is converted by
`BlenderModelConverter` and compared model by model with `ModelSignature`: draws in scene order
(triangles, bounds — rigid in world space, skinned in rest pose through joint world × inverse bind,
since glTF ignores a skinned mesh node's transform and Blender may bake it into positions and
reorder joints — and material name, factors and texture bytes), joints as a name set with rest world
transforms, and clip names, to within 1e-3. A member that differs keeps its GLB and the rest are
rebuilt; a family is refused when its members disagree on `[extract] directory` or `[glb] optimize`,
or when anything but a model document references a member. A verified `.blend` is moved into place
with a sidecar (the GLB's own identity for a singleton, a new one for a family), each member's
record under its asset with materials remapped by name and images by bytes, clip settings keyed by
asset and remapped by name, and the conversion stored as `ModelSource` would; model documents are
repointed, stale paths of other referrers repaired, references minted, and the GLBs and their
sidecars removed. `--dry-run` deletes what it staged. A target that already exists is refused.

`ImportContext.BuiltPath` asks the referenced asset's own importer where output lands. Textures
become KTX2, prefabs/configs use the profile extension, and mesh/skeleton/clip/material/audio/binary
retain their paths. Built `.material` uses TOML or JSON by profile, detected by its first character.
Both prefab and material baking use this API; runtime readers never derive paths by convention.

### Navigation baking

The canonical baked navigation asset suffix is `.navmesh`. The navmesh importer validates its
Detour MeshSet and copies it unchanged to the same relative built path. A prefab's navigation
reference uses the asset's sidecar GUID and its `.navmesh` path; the old `.navmesh.bin` suffix is
not an importer input.

`SceneNavigationBaker` bakes from the canonical level prefab. It expands prefab instances and
composes world transforms (`SceneGeometry`), then reads each mesh document as the cooked mesh
the build writes for it: `CookedMeshes` runs the same cook step as the `.mesh`/`.skinnedmesh`
importers, so rigid draws arrive with their node transforms baked and a `.blend` asset's mesh
comes from its converted GLB. Each model is cooked once per bake; a built blob is never read
back, because only a build computes the index environment that proves one current. Schema
fields marked `authoredBy: mesh` supply geometry;
`authoredBy: navmesh-geometry` booleans exclude whole subtrees, and a field marked
`authoredBy: navmesh-body` names a `PhysicsBodyType` — dynamic and kinematic bodies exclude
their subtree the same way. Skinned geometry is excluded. A mirrored placement flips triangle
winding; the cooked mesh's own winding is kept. Unresolved or malformed geometry fails the bake
rather than producing a partial result.

The generated path replaces the level's `.prefab` extension with `.navmesh`. The baker updates
the component's `authoredBy: navmesh` string field and preserves unrelated canonical data. It
stages output and checks the document has not changed before publishing. Publication also
stages the output's `.meta`: minted with `importer = "navmesh"` when absent, while an existing
identity and a recorded importer are kept, so `assets verify` passes without waiting for the
watcher. `Normalize` updates only the generated field; `Preview` reads the existing derived
binary without baking.

Editors invoke the component's C# methods through the [authored action contract](#authored-editor-actions).
Those methods call `SceneNavigationBaker` for Bake, Preview and generated-path normalization;
the component also decides whether its save hook bakes according to the saved toggle state.

`Paradise.Export.NavMesh.NavMeshBakeService` owns the underlying Recast bake, Detour
serialization and preview extraction. Its `NavMeshBakeInput` carries world-space triangles in
the engine's right-handed, Y-up coordinates, in meters, with upward-facing walkable winding.
`NavMeshBakeSettings` defaults to `CellSize = 0.2`, `CellHeight = 0.1`, `AgentRadius = 0.35`,
`AgentHeight = 1.8`, `MaxClimb = 0.3` (meters) and `MaxSlope = 45` (degrees). Coordinates and
settings must be finite; agent height must span at least three cell-height voxels, climb must
be less than agent height, and slope must be at least zero and below 90 degrees. The single-tile
bake permits at most 16,777,216 XZ cells and 8,191 vertical voxels. Reduce geometry bounds or
increase cell size/height when a bake exceeds these limits.

`NavMeshPreview` contains vertices and triangle indices for the baked walkable surface in the
same coordinates as the input. Authored actions return this geometry as a generic viewport
overlay; editors convert it for display without rebaking or interpreting the binary.

A geometry or bake failure leaves the existing binary, sidecar and canonical document intact.
The scene service stages each output beside its destination, refuses a document changed during
the bake, and replaces destinations atomically. If publishing the generated component path
fails after the binary was replaced, it restores the previous binary and sidecar. The outputs
are separate files, so their publication is not a single filesystem transaction.

### Authored editor actions

Components extend editor controls through public static C# methods:

```csharp
[AuthoredButton(DisplayName = "Bake")]
[AuthoredOnSave]
public static void Bake(AuthorActionContext context) { /* project-specific work */ }

[AuthoredToggle(DisplayName = "Auto-bake on Save")]
public static void AutoBake(AuthorActionContext context, bool enabled)
{
    context.Result.Toggles[nameof(AutoBake)] = enabled;
}

[AuthoredOnSave]
public static void BakeAfterSave(AuthorActionContext context)
{
    if (context.ToggleValues.GetValueOrDefault(nameof(AutoBake)))
        Bake(context);
}

[AuthoredPreview(DisplayName = "Preview")]
public static AuthorActionOverlay Preview(AuthorActionContext context)
{
    return new AuthorActionOverlay
    {
        Id = "surface",
        Vertices = [-1f, 0f, -1f, -1f, 0f, 1f, 1f, 0f, -1f],
        Indices = [0, 1, 2],
        Color = [0.15f, 0.8f, 0.35f, 0.35f],
    };
}
```

Buttons also accept no parameters, and toggles may accept just `bool`; both return `void`.
Save hooks take the button signature. Preview providers accept either no parameters or one
`AuthorActionContext` and return an `AuthorActionOverlay`. Invalid, ambiguous, generic, async,
or by-reference signatures produce `PAUT013`. The generated schema publishes `actions` with
method name, display name, kind (`button`, `toggle`, `preview`, or `save`), and documentation.
An `[AuthoredOnSave]` method publishes a `save` entry — alongside its control entry under the
same name when a button or toggle attribute is present — and `save` entries draw no inspector
control. Editors dispatch every `save` entry after a successful save — a marked toggle is
re-invoked with its stored value — and C# decides what the hook does from `context.ToggleValues`
and `context.IsSave`.

```sh
paradise assets invoke-action assets/levels/arena.prefab COMPONENT_GUID Bake \
  --entity OBJECT_GUID --state state.json --response response.json
```

`--value true|false` invokes a toggle; `--on-save` invokes a declared save hook. The optional
state file is a JSON object mapping toggle names to booleans. The CLI discovers and, if needed,
builds the configured game host. For an explicitly built host, pass `--assembly /absolute/host.dll
--no-build`. Only annotated methods can be invoked.

Preview providers use the same `invoke-action` command with neither `--value` nor `--on-save`.
The CLI invokes the provider and wraps its returned overlay in the ordinary action response,
setting `visible` to true. A provider returns geometry instead of mutating `context.Result`;
null returns, result mutations, malformed triangles and invalid colors fail the invocation.
Vertices must be finite XYZ triples, indices must form complete triangles within the vertex
array, and RGBA values must be finite and between zero and one. Empty geometry is valid.
The editor owns visibility for each component/object/provider, persisted outside the canonical
prefab. Enabling a preview requests fresh geometry from C#; disabling it hides the overlay
locally. Enabled previews refresh after successful actions, saves and document reloads. Preview
methods provide geometry and do not implement visibility toggles or save hooks.

`AuthorActionContext` supplies host paths for the project and document, component/object IDs,
requested value, save-hook status, and current toggle state. Methods mount content themselves.
On success the CLI writes `AuthorActionResult`: `toggles`, `documentChanged`, and named
`overlays` of flat world-space `vertices`, triangle `indices`, RGBA `color`, and `visible`.
Editors validate the response before applying it, preserve toggle state outside the canonical
prefab, and convert overlay coordinates only for display. Failed actions do not publish a response.
The triangle transport uses engine right-handed, Y-up coordinates and is independent of editor
APIs. The Blender adapter currently renders it; other editors need their own adapter to use these
controls and overlays.

### Animation contracts

`Paradise.Animation` is a managed ozz-animation port pinned to 0.17: `ozz-skeleton` v2 and
`ozz-animation` v7. Archives are persisted; runtime blobs are not. Builders/optimizer/converter
are managed; runtime skeletons, animations, poses and sampling contexts are unmanaged BLOB layouts.

`SamplingContext.Sample` and `LocalToModel.Compute` allocate nothing. Four-track `Vector128`
interpolation retains ozz's SoA layout; cursor walking remains scalar and variable-index lane
extraction uses a stack store. `AnimationPlayer` keeps clip references and playback/fade state in
the class; one native blob per character owns sampling contexts, poses and matrices. Hosts call `Advance` then
`Evaluate`, and use `SkinningPalette.Compute` for GPU palettes. Use `JointPoses` batch operations
in hot paths; its indexer gathers one joint for attachments/tests.

The skeleton contains the GLB's whole node tree in depth-first, parents-first order, with siblings
ordered by glTF index. Mesh skins map palette slots to joints and inverse binds. Unanimated joints
hold rest pose. `ClipConverter` fills it before the ozz builder's identity padding, bakes STEP holds
and inserts slerped keys for rotation arcs wider than 15°. Quantization is 16-bit; optional sidecar
`[glb] optimize = { tolerance, distance }` uses ozz's 1 mm / 10 cm defaults.

`OzzParityTests` compares generated archives byte-for-byte against native fixtures. The glTF
reference sampler lives only in pipeline tests. `Paradise.Animation.Benchmarks` compares blob,
managed and glTF runtimes; `PARADISE_OZZ_NATIVE` selects the native shim and
`PARADISE_BENCHMARK_GLB` selects a character asset.

### Importers and extraction

- `ImporterChain` alone walks importers. `Claims` reads only path/header data; sidecar creation
  records the chosen importer. Never overwrite a recorded name or fall back from an unknown name.
  Keep importer extension guards for hand-edited sidecars; a declined import is a build error.
- Builds must not edit committed sidecars to choose an importer. Build-time reconciliation uses
  `RewriteSources = false`: sidecar identity repair may not move authored paths or container URIs.
- Watchers mint tool-owned GLB part documents; `extract` may overwrite a stale part belonging to
  that GLB, never one belonging to another. Materials, textures and prefabs become authored when
  created and only `extract` writes them.
- Sidecar extraction fingerprints track both container and document state; material fingerprints
  cover only glTF-expressible fields. Refuse extraction when both sides changed.
- KTX2 is build output only; `verify` rejects it beneath `assets/`.
- Read sidecar inline tables through both `CanonicalTomlTable` and `CanonicalInlineTable` because
  root-level inline tables may deserialize as generic tables.

### References

`ReferenceGraph` is derived from `AssetIndex` and importer-declared `References`, never persisted.
Document reference lists stay in documents. Container references live in `MeshImportSettings`
sidecar data because source containers cannot always be rewritten. Preserve edges to missing
identities and their paths so diagnostics can identify their referrers.

- `mv` follows `DependentsOf` plus `Unreadable` assets through the importer rewrite API.
- `rm` refuses referenced assets unless forced; it never clears a reference slot.
- Watchers follow moved identities' dependents, deferring and retrying files still in debounce.
- Use `ReferenceChain` and `IAssetImporter.Rewrite`; verbs must not branch on extensions.
- Unrecorded container URIs remain `PathOnly`; moves follow only those they touched and report
  unresolved cases without rewriting unrelated sources.

# Asset pipeline and animation

### Identity and outputs

`AssetReference` is `{ guid, path }`: **GUID identifies; path is a readable hint**.
Pass `AssetReference` across reference APIs, not a raw path.
Share one `AssetIndex` scan across build, bake, resolve, verify and repair. Resolve with
`AssetIndex.AssetOf`, never `assetsRoot / reference.Path`; key caches and cycle checks by GUID.
Stale paths after external renames are warnings, repairable with `verify --fix`; missing GUIDs are
errors. `assets mv` updates hints eagerly while retaining sidecar identity.

A GLB is source only and builds no output. Tool-owned `.mesh`, `.skinnedmesh`, `.skeleton` and
`.anim` documents name its parts with `{ source, slot, name, index, hash, skeleton }`. Prefabs
reference those documents, not the GLB. Meshes cook to aligned native MeshBlob data (magic/version
first); skeletons and clips cook to ozz archives. Clip lookup uses name, then content hash, then index.

The GLB determines rigid versus skinned kind. A skinned document names its `.skeleton`; MeshBlob
v3 stores that skeleton's **built path**. Kind mismatches are build errors; when the GLB gains or
loses its rig, replace the stale document with a fresh identity.

`ImportContext.BuiltPath` asks the referenced asset's own importer where output lands. Textures
become KTX2, prefabs/configs use the profile extension, and mesh/skeleton/clip/material/audio/binary
retain their paths. Built `.material` uses TOML or JSON by profile, detected by its first character.
Both prefab and material baking use this API; runtime readers never derive paths by convention.

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

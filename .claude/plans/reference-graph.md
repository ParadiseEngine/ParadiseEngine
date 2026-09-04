# Plan: references by identity, end to end — graph, material-owned textures, cooked meshes

Three stages, three PRs, in dependency order. Each is shippable on its own; hosts (ShiningPie,
ParadiseGodotEditor, Pingu, the Blender addon) follow each stage after its engine package ships,
never inside the same PR (CI builds them against published packages).

| Stage | PR | Closes | Depends on |
|---|---|---|---|
| 1. Reference graph + GLB references + rename/delete | `feat/reference-graph` | — | #243 |
| 2. Material documents own their textures | `feat/material-textures` | — | stage 1 |
| 3. Cook GLB to mesh + animation blobs; runtime never opens `.glb` | `feat/mesh-cook` | #186 | stage 2 |

## Decisions already taken (2026-09-04, with the user)

- The graph is DERIVED from `AssetIndex` + the documents. Never stored in sidecars (a second copy
  kept in sync by a watcher that may not be running, dirtying two files per edit). Not persisted
  under `.editor/` until a project makes the walk measurable (ShiningPie: 30 documents).
- The watcher DOES rewrite documents after a rename, so a Finder rename leaves a tidy tree.
- A forced delete leaves dangling references in place and reports them; no slot is nulled.
- GLBs carry their texture references as `{ guid, path }` in `images[i].extras.paradise`, stamped
  into the source GLB under `assets/`; the `uri` is kept updated as the DCC hint.
- The runtime ends up relying on material documents (mesh, texture, shader by guid), not on the
  glTF material; and on cooked mesh/animation blobs, not on GLB. Staged, not one PR.

## What exists (after #243)

- `AssetIndex`: one scan per run — every file under `assets/`, guid → path from sidecars,
  `Resolve(reference)` with the guid deciding and the path a hint.
- `DocumentReferences.Enumerate/Rewrite`: every `{ guid, path }` in a PREFAB document.
  Material documents (`materials/*.toml`) go through the `config` importer and are NOT walked.
- `MeshTextureReferences.Rewrite(glb).Sources`: the image uris inside a GLB. Path only.
- `mv` walks every document; `verify --fix` walks every document; the watcher carries sidecars only;
  a delete is quarantined 30 s then forgotten, and nobody learns who referenced it.
- Runtime: `GltfSceneReader.Read` in four places (ShiningPie `SceneAssets`/`LevelAssets`, Godot
  `Paradise.Sample.Runtime`, Pingu `PoolSceneRenderer`, `PbrViewerScene`). Textures come from the
  GLB's images; a textured glTF material WINS over the document material, whose texture fields
  (`LevelMaterialData.*Texture`) nothing fills.

---

# Stage 1 — reference graph, GLB references, rename / move / delete

## 1.1 GLB texture references: `extras.paradise`

Every `images[i]` with an external `uri` gets `extras.paradise = { guid, path }` — the same shape
as every other reference, so `AssetReferenceCodec` reads it after a JSON→canonical hop.

- **Writers.** (a) The Blender addon's GLB exporter stamps it at export (it knows the texture's
  sidecar guid) — addon follow-up, not this PR. (b) The watcher's reconcile and `verify --fix`
  stamp a GLB that lacks it, resolving `uri` → the sidecar beside that texture, the way a missing
  sidecar is minted. A uri that resolves to nothing is left unstamped and is a verify finding.
- **Rewrite.** `GlbBinary.Write` already re-emits the JSON chunk. `MeshTextureReferences` gains
  `Stamp(glb, resolve)` and `FollowUris(glb, index)`; the build's `.png → .ktx2` repoint keeps
  working on the uri and ignores extras.
- **Rules.** The guid decides: a uri that no longer matches where its guid lives is a stale hint,
  rewritten by `mv`, the watcher, and `verify --fix`; today's "mv cannot rewrite a mesh" warning
  goes away. A GLB with embedded images has nothing to stamp. Stamping changes the source file's
  bytes, which the build index sees as a change (one rebuild, then stable).

## 1.2 `ReferenceGraph` (Paradise.Assets.Pipeline)

```csharp
public sealed class ReferenceGraph
{
    public static ReferenceGraph Build(IFileSystem fs, AssetProjectLayout layout, AssetIndex index, AssetIgnoreRules ignore);
    public IReadOnlyList<ReferenceEdge> Edges { get; }
    public IReadOnlyList<UPath> Unreadable { get; }                   // documents/GLBs that would not parse
    public IReadOnlyList<ReferenceEdge> DependentsOf(Guid asset);      // who references it
    public IReadOnlyList<ReferenceEdge> DependenciesOf(Guid referrer); // what it references
    public IReadOnlySet<Guid> TransitiveDependentsOf(Guid asset);
    public void Replace(Guid referrer, IEnumerable<ReferenceEdge> fresh); // watcher: one file re-read
    public void Forget(Guid referrer);
}
public readonly record struct ReferenceEdge(Guid Referrer, UPath ReferrerPath, Guid Target, string Where, string Path);
```

- Nodes are guids; paths are hints. Referrer identity is the referrer's own sidecar guid, so a
  renamed level keeps its edges. A referrer without a sidecar is `Unreadable`.
- Edges whose target no guid carries are KEPT with the path as written, so `refs` and delete
  reporting can name a dangling reference — the moment you most want to ask "who pointed here".
- Sources of edges: prefab documents (`DocumentReferences.Enumerate`) and GLB `extras.paradise`
  (1.1). Material documents join in stage 2.
- Pure over `IFileSystem`; tests on `MemoryFileSystem`.

## 1.3 `mv` rewrites only dependents

After the files move, `AssetMover` builds the graph and rewrites `DependentsOf(each moved guid)`
(documents and GLB uris), not every file. The moved documents' own references are assets-relative
and need no change. `MoveResult` keeps its shape.

## 1.4 `refs` verb

`paradise assets refs <path> [--transitive]` prints dependents then dependencies, one edge per
line: `prefabs/box.prefab: in ShiningPie.Authoring.ObstacleMesh.Mesh -> Models/Prim_Cube.glb (guid)`.
Exit 0; it is a query. The Blender mirror's "is this prefab referenced?" becomes one CLI call.

## 1.5 The watcher rewrites after a rename

In `AssetWatcher.Drain`, after `Carry` returns `Carried`/`Relinked`: rescan, then
`ReferenceRepair.Fix` over `DependentsOf(carried guid)` only (new overload taking the documents),
GLB uris included. Logged as `rewrote:` like `mv`. Rules: skip a document that is itself pending
in the debounce window (retry next drain); a rewrite re-enters as a change, which is correct and
finite; a document open in Blender sees its stamp move and the addon's "changed on disk" refusal
does its job; `--dry-run` writes nothing.

## 1.6 Delete: report, and `rm`

- Watcher: when `Expire` drops a quarantined identity, log every dependent from the in-memory graph
  (`deleted: models/crate.glb — still referenced by prefabs/box.prefab in …Mesh`). No write.
- `paradise assets rm <path> [--force] [--dry-run]`: refuses when `DependentsOf` is non-empty
  (prints them, exit 1); `--force` deletes asset + sidecar and prints the now-dangling references
  for `verify` to keep reporting. A directory is refused unless nothing outside it references
  anything inside it.

## 1.7 Tests and docs

`ReferenceGraphTests` (edges; dangling kept; transitive through a nested prefab; GLB edges;
Replace/Forget; Unreadable). `MeshTextureReferencesTests` (stamp; follow; embedded untouched;
idempotent). `AssetMoverTests` (an unrelated document's stamp is unchanged; a GLB uri follows).
`AssetWatcherTests` (rename → dependents rewritten; pending skipped then caught; dry-run).
`AssetRemoverTests`. `BuildHost` parsing for `refs`, `rm`, `--transitive`, `--force`.
Docs: `AGENTS.md`/`README.md` verb list; watcher and mover remarks; `extras.paradise` in the
contract docs. Full suite green; end-to-end on a temp ShiningPie copy.

---

# Stage 2 — material documents own their textures

Today the glTF material inside the GLB is the only thing that binds textures, and it beats the
document. After this stage the DOCUMENT is the material: factors and textures, textures as
references. The glTF material becomes an import-time seed and nothing at runtime reads it.

## 2.1 Contract

- Authoring (`materials/*.toml`): `BaseColorTexture`, `MetallicRoughnessTexture`, `NormalTexture`,
  `OcclusionTexture`, `EmissiveTexture` become `{ guid, path }` inline tables (`{}` = none), plus
  `BaseColorUvTransform` (offset/scale/rotation; glTF `KHR_texture_transform`) which today only the
  GLB can express. `Paradise.Export.Data.LevelMaterialData` keeps its string paths on the BAKED side
  (`build/`), rebased to the KTX2 like every reference; schema version bump in lockstep with the
  Blender addon's `contract/schema.py` and the Godot exporter.
- `DocumentReferences` learns material documents (and any config document: walk every inline
  `{ guid, path }`), so the graph, `mv`, `--fix`, and `rm` cover them with no new code paths.

## 2.2 Pipeline

- `config` importer: a material document's texture references resolve by guid through
  `ImportContext.Resolve` (dependency recorded), and bake to the texture's built KTX2 path.
- **Seeding.** `paradise assets materials seed <glb>` (and a watcher/reconcile variant behind a
  flag): for each glTF material in a GLB with no document of that name, write
  `materials/<name>.toml` from its factors and its images' `extras.paradise` guids (stage 1.1).
  Idempotent: an existing document is never overwritten. This is how ShiningPie's 40-odd
  textured materials get documents without hand-typing.
- `verify`: a material document naming a texture whose guid nobody carries is an error; a mesh
  whose primitive `i` has no slot in `MaterialsComponentData.Slots` is a warning naming the mesh.

## 2.3 Runtime (hosts, after the package ships)

- `Paradise.Rendering.Pbr.MaterialResourceCache` gains `AddMaterial(LevelMaterialData, resolveTexture)`
  that binds KTX2 by path from the document; the `GltfMaterialData` overload stays for the sample
  viewer only.
- ShiningPie `SceneAssets.Instantiate`: the "textured GLB material wins" branch is deleted; slot
  `i` binds the document or the fallback. Same in Godot `SceneAssembler` and Pingu.
- Blender addon: material panel edits texture slots as asset references (the field widgets exist);
  the GLB exporter stamps `extras.paradise` (1.1a).

## 2.4 Open questions to settle when stage 2 is planned in detail

1. Does a GLB material with textures but no document still render textured (compat), or is a
   missing document a verify error? (Proposed: error after seeding exists.)
2. Texture sampler settings (wrap, filter): document fields, or the texture's sidecar `[texture]`
   domain? (Proposed: sidecar, since they are per-texture.)
3. Shader/material kind by guid ("shader with GUID"): is a shader an asset under `assets/` with a
   sidecar, or still the `MaterialKind` string? Needs the Slang pipeline's view.

---

# Stage 3 — cook GLB to mesh + animation blobs (#186)

Issue #186 is the spec. The source under `assets/` stays GLB (interchange, and stage 1.1 keeps its
uris honest for the DCC); `build/` gets a mesh blob, a skeleton archive and clip archives; the
runtime never opens `.glb` and `Paradise.Assets.Gltf` leaves the play path.

## 3.1 Shape (from #186, restated)

- `Paradise.Assets.Mesh`: blob reader (magic + version, AABB, engine-layout vertex buffers
  static/skinned, u16/u32 indices, draw records `firstIndex/indexCount/materialSlot`, skin palette
  in skeleton order). Slot `i` == glTF primitive `i` is a cook invariant.
- Cook step in `Paradise.Assets.Pipeline` replacing the GLB copy-through in the `mesh` importer:
  `GltfSceneReader` → blob (+ meshoptimizer-style reorder as an algorithm). Incremental via the
  build index (source bytes + cook argv + tool versions).
- Skeleton + clips: ozz archives. Runtime sampling through `Paradise.Animation.Ozz`.
- `RenderableComponentData.Mesh` (baked) points at the blob; documents keep referencing the source
  GLB by guid, the build rebases — the PNG→KTX2 pattern, which stage 1's graph already models.
- Materials come from stage 2, so the cook carries NO material or image data: geometry, skin,
  clips only. That is why stage 2 goes first.

## 3.2 Open questions (answer when stage 3 is planned; each is a PR-shaping decision)

1. ozz interop: native + P/Invoke (AOT, like libktx) vs. a C# reader of ozz archives.
2. Clip container: raw `.ozz` vs. a Paradise header (guid, name, duration, skeleton id).
3. Blob layouts: start with two (static, skinned).
4. Multi-mesh GLB: one blob per source asset with draws + node-name table (recommended in #186).
5. Tooling split: shell out to `gltf2ozz` vs. one C# cook.
6. Keep a `glb` build profile for editor preview, or blob everywhere.

## 3.3 Acceptance

#186's checklist verbatim, plus: the reference graph shows a level → GLB edge unchanged (the
document still names the source), and `mv` of a GLB rebuilds only its blob and its dependents.

---

## Out of scope for all three stages

Persisting the graph under `.editor/`; nulling references on delete; morph targets; animation
state machines (#164); Draco/meshopt as a shipping format.

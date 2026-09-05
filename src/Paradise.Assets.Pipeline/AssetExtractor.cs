using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one <c>extract</c> did: the authored files it wrote, the ones it left because they were the author's, and what stopped it.</summary>
/// <param name="HasAuthoredParts">Whether the GLB holds anything only the verb writes — glTF materials or embedded images — as read on this run, so a caller need not parse it again to know whether to offer <c>extract</c>.</param>
public sealed record ExtractResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ExtractedFile> Written,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Warnings,
    bool HasAuthoredParts = false);

/// <summary>A file <c>extract</c> wrote, and why when it was not a first write: the path stays a path, so a consumer that resolves it never sees the note.</summary>
/// <param name="Path">Relative to <c>assets/</c>.</param>
public sealed record ExtractedFile(string Path, string? Note = null)
{
    public override string ToString() => Note is null ? Path : $"{Path} ({Note})";
}

/// <summary>Which side wins when both the GLB and an extracted document changed since their last sync; the default is to refuse and say so.</summary>
public enum ConflictResolution
{
    Refuse,
    TakeGlb,
    TakeDocument,
}

/// <summary>
/// The <c>extract</c> verb: what a GLB holds becomes authored assets beside it — a mesh, skeleton
/// and clip reference document per part the build cooks from the GLB, a material document per
/// glTF material, the embedded textures as files — and a prefab that wires them together. The
/// GLB stays the one source of its geometry; everything downstream references the documents.
/// </summary>
/// <remarks>
/// <para>
/// The reference documents are tool-owned and carry no author work, so the watcher mints them
/// for a new or re-exported GLB (<see cref="MintReferences"/>). Materials, textures and the
/// prefab are the author's from the moment they are written, so those are this verb's alone:
/// a build doing that per save would edit committed files under an author. Idempotent: a second
/// run writes nothing and reports what it kept. A file this verb did not create is adopted when
/// it holds what the GLB extracts to, and is otherwise refused — not recorded, not overwritten,
/// not bound — until the author deletes it or names a side with a flag.
/// </para>
/// <para>
/// A material or image has two sides that can change under each other, so its entry is recorded
/// with a FINGERPRINT of each as of the last sync — the GLB side is the hash of what the GLB
/// would extract to now, the document side the hash of the file's parsed values — and the next
/// run tells "the GLB was re-exported" from "the author edited the document": the first
/// re-extracts, the second is the document's to keep (and, for a material, to write back), and
/// both at once is a conflict the author resolves by name.
/// </para>
/// </remarks>
public static partial class AssetExtractor
{
    public static ExtractResult Extract(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        UPath glb,
        IReadOnlyList<IAssetImporter>? importers = null,
        ConflictResolution resolution = ConflictResolution.Refuse,
        ILogger? logger = null,
        bool generatePrefab = true,
        SidecarMaintainer? maintainer = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        glb.AssertAbsolute(nameof(glb));

        var chain = importers ?? AssetImporters.All;
        var log = logger ?? NullLogger.Instance;
        var run = new Run(fileSystem, layout, glb, chain, resolution, log, generatePrefab, referencesOnly: false, maintainer);
        return run.Execute();
    }

    /// <summary>Whether the GLB holds anything only the verb writes — glTF materials or embedded images; the reference documents the watcher mints are not that.</summary>
    public static bool HasAuthoredParts(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);
        try
        {
            if (GltfSceneReader.ReadGeometry(glb).Materials.Length > 0) return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            return false;   // the build's or extract's error to name
        }

        return GlbTextureRewriter.TryListEmbedded(glb, "", out var embedded, out _) && embedded.Count > 0;
    }

    /// <summary>
    /// Only the mesh, skeleton and clip reference documents, for the watcher: they carry no author
    /// work, so minting them on a save is the same class of action as minting a sidecar. Nothing
    /// an author edits is touched, and a foreign document is reported, never overwritten.
    /// </summary>
    /// <param name="maintainer">The one minting authority: the watcher's own, so a document re-minted inside the quarantine window gets its held identity back. A caller with no watcher alive passes none and one is made for the run.</param>
    public static ExtractResult MintReferences(
        IFileSystem fileSystem,
        AssetProjectLayout layout,
        UPath glb,
        IReadOnlyList<IAssetImporter>? importers = null,
        ILogger? logger = null,
        SidecarMaintainer? maintainer = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        glb.AssertAbsolute(nameof(glb));

        var run = new Run(fileSystem, layout, glb, importers ?? AssetImporters.All, ConflictResolution.Refuse, logger ?? NullLogger.Instance, generatePrefab: false, referencesOnly: true, maintainer);
        return run.Execute();
    }

    private sealed class Run(IFileSystem fileSystem, AssetProjectLayout layout, UPath glb, IReadOnlyList<IAssetImporter> chain, ConflictResolution resolution, ILogger log, bool generatePrefab, bool referencesOnly, SidecarMaintainer? sharedMaintainer)
    {
        private readonly List<string> _errors = [];
        private readonly List<ExtractedFile> _written = [];
        private readonly List<string> _kept = [];
        private readonly List<string> _warnings = [];
        private readonly List<UPath> _minted = [];
        private ExtractSettings _extract = ExtractSettings.None;

        public ExtractResult Execute()
        {
            if (!glb.IsInDirectory(layout.Assets, recursive: true)) return Fail($"'{glb}' is not under {layout.Assets}; extract works on assets only");
            if (!fileSystem.FileExists(glb)) return Fail($"'{glb}' does not exist");
            if (!MeshContainer.IsMesh(glb)) return Fail($"'{glb.GetName()}' is not a GLB; extract reads .glb only (export JSON glTF as .glb)");

            ProjectManifest manifest;
            try
            {
                manifest = ProjectManifest.Load(fileSystem, layout.Manifest);
            }
            catch (ProjectManifestException error)
            {
                return Fail(error.Message);
            }

            _extract = manifest.Extract;
            var index = AssetIndex.Scan(fileSystem, layout.Assets, manifest.Ignore);
            var sidecarPath = SidecarMeta.PathFor(glb);
            if (!fileSystem.FileExists(sidecarPath)) return Fail($"'{index.Relative(glb)}' has no sidecar yet; run `paradise assets watch` (or `verify --fix`) to mint one, then extract");
            var meta = SidecarMeta.Load(fileSystem, sidecarPath);
            var settings = GlbImportSettings.ReadExtraction(meta);

            var directory = Directory(index, settings, manifest);
            var stem = Path.GetFileNameWithoutExtension(glb.GetName());

            // Sidecars for what is written are minted as it is written: the materials name the
            // extracted textures by guid, and the prefab names everything by guid. Through the
            // caller's maintainer when there is one — a second authority with its own quarantine
            // memory would mint a fresh identity for a document the watcher is holding.
            var maintainer = sharedMaintainer ?? new SidecarMaintainer(fileSystem, layout, log, ignore: manifest.Ignore, importers: chain);
            AssetIndex Rescan()
            {
                foreach (var path in _minted) maintainer.Ensure(path);
                return AssetIndex.Scan(fileSystem, layout.Assets, manifest.Ignore);
            }

            var bytes = fileSystem.ReadAllBytes(glb);
            var images = settings.Images.ToList();
            if (!referencesOnly)
            {
                bytes = Textures(index, directory, stem, bytes, settings, out images);
                index = Rescan();
                if (_errors.Count > 0) return Abort(index, sidecarPath, settings with { Images = images });
            }

            GltfAsset asset;
            CookedGlb cooked;
            try
            {
                asset = GltfSceneReader.ReadGeometry(bytes);
                _hasAuthoredParts = asset.Materials.Length > 0 || (GlbTextureRewriter.TryListEmbedded(bytes, stem, out var embeddedImages, out _) && embeddedImages.Count > 0);
                cooked = GltfCook.Cook(asset);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                _errors.Add($"{index.Relative(glb)}: {error.Message}");
                return Abort(index, sidecarPath, settings with { Images = images });
            }

            var recorded = settings;
            var source = new AssetReference(meta.Guid, index.Relative(glb));
            var skeleton = cooked.Skeleton is null ? null : Document(index, Target(index, recorded.Skeleton, directory / $"{stem}.skeleton"), new MeshReferenceDocument(source, MeshSlot.Skeleton), recorded.Skeleton);
            var mesh = MeshDocument(ref index, Rescan, directory, stem, source, cooked, recorded.Mesh, skeleton);
            var clips = Clips(index, directory, stem, source, cooked, recorded);
            if (referencesOnly)
            {
                index = Rescan();
                var minted = Identified(index, recorded with { Mesh = mesh, Skeleton = skeleton, Clips = clips });
                // Every drain of a GLB comes through here; the steady state must not touch the sidecar.
                if (!Same(minted, recorded)) Save(index, sidecarPath, minted);
                return Finish();
            }

            var materials = Materials(index, directory, stem, bytes, asset, recorded);

            index = Rescan();
            var extraction = new GlbExtraction(settings.Directory, mesh, skeleton, clips, materials, images, recorded.Prefab);
            if (_errors.Count > 0) return Abort(index, sidecarPath, extraction);

            var prefabPath = Target(index, recorded.Prefab, directory / $"{stem}.prefab");
            if (generatePrefab) extraction = extraction with { Prefab = Prefab(index, layout, prefabPath, stem, meta.Guid, Identified(index, extraction), cooked, asset) };
            if (extraction.Prefab is { } prefab && prefab.Guid == Guid.Empty) index = Rescan();

            Save(index, sidecarPath, extraction);

            // The GLB's own image references, by identity: an image that just left the container
            // is an external uri now, and the sidecar records it like any other.
            MeshReferences.Apply(fileSystem, glb, MeshReferences.Reconcile(fileSystem, index, glb), rewriteContainer: false);
            return Finish();
        }

        /// <summary>
        /// The record is saved on the way out even when an entry refused: the ones that resolved
        /// were already written, and a sidecar that does not say so reports them as conflicts the
        /// author never made on the retry. A refused image or material keeps its last sync, so it
        /// re-detects; a refused document keeps its last reference, so it is still followed.
        /// </summary>
        private ExtractResult Abort(AssetIndex index, UPath sidecarPath, GlbExtraction extraction)
        {
            Save(index, sidecarPath, extraction);
            return Finish();
        }

        private static bool Same(GlbExtraction a, GlbExtraction b)
            => a.Mesh == b.Mesh && a.Skeleton == b.Skeleton && a.Prefab == b.Prefab && a.Directory == b.Directory
                && a.Clips.SequenceEqual(b.Clips) && a.Materials.SequenceEqual(b.Materials) && a.Images.SequenceEqual(b.Images);

        private void Save(AssetIndex index, UPath sidecarPath, GlbExtraction extraction)
        {
            var meta = SidecarMeta.Load(fileSystem, sidecarPath);
            GlbImportSettings.WriteExtraction(meta, Identified(index, extraction));
            meta.Save(fileSystem, sidecarPath);
        }

        private UPath Directory(AssetIndex index, GlbExtraction settings, ProjectManifest manifest)
        {
            var relative = settings.Directory ?? manifest.Extract.Directory;
            return relative is null ? glb.GetDirectory() : (layout.Assets / relative).ToAbsolute();
        }

        /// <summary>
        /// Embedded images become files beside the GLB and the GLB points at them: the file IS the
        /// texture now, and the DCC re-imports it as such. Each is an entry under the sync rule like
        /// a blob, so a re-export with new pixels re-extracts, and a file that is not this GLB's —
        /// another GLB's, or the author's — is never what the GLB gets rewritten to point at.
        /// </summary>
        private byte[] Textures(AssetIndex index, UPath directory, string stem, byte[] bytes, GlbExtraction recorded, out List<GlbExtraction.NamedEntry> images)
        {
            images = [];
            if (!GlbTextureRewriter.TryListEmbedded(bytes, stem, out var embedded, out var problem))
            {
                _errors.Add($"{index.Relative(glb)}: {problem}");
                return bytes;
            }

            images = recorded.Images.ToList();
            if (embedded.Count == 0) return bytes;

            var uris = new Dictionary<int, string>();
            foreach (var image in embedded)
            {
                if (image.IsKtx2)
                {
                    _errors.Add($"{index.Relative(glb)}: image #{image.Index} is KTX2; an authored texture is a PNG or JPEG, and KTX2 is build output — re-export with the source image");
                    continue;
                }

                var previous = recorded.Images.FirstOrDefault(i => i.Index == image.Index)?.Entry;
                var path = Target(index, previous?.Reference, directory / $"{stem}_{image.Index}{image.SourceExtension}");
                var entry = Blob(index, path, image.Bytes, previous, "image");
                images.RemoveAll(i => i.Index == image.Index);
                if (entry is not null) images.Add(new GlbExtraction.NamedEntry(image.Index, ImageSlot(image.Index), entry));
                uris[image.Index] = MeshContainer.UriFor(index.Relative(glb), index.Relative(path));
            }

            if (_errors.Count > 0) return bytes;

            if (!GlbTextureRewriter.TryExternalizeSources(bytes, embedded, uris, out var rewritten, out var error))
            {
                _errors.Add($"{index.Relative(glb)}: {error}");
                return bytes;
            }

            fileSystem.WriteAllBytes(glb, rewritten);
            _written.Add(new ExtractedFile(index.Relative(glb), "images now external"));
            return rewritten;
        }

        private static string ImageSlot(int imageIndex) => $"images[{imageIndex}]";

        /// <summary>
        /// The geometry document: a <c>.skinnedmesh</c> naming its skeleton when the GLB has a
        /// skin, a <c>.mesh</c> otherwise. The GLB decides the kind, so a recorded document of the
        /// OTHER kind (a rig added or removed in the DCC, or a tree from before skinned meshes were
        /// their own kind) is replaced under a fresh identity and the stale one removed — a
        /// reference to it is then a verify finding rather than a silently wrong blob, and the
        /// referencing prefab has to change anyway, since a rigged mesh is a different component.
        /// </summary>
        private AssetReference? MeshDocument(ref AssetIndex index, Func<AssetIndex> rescan, UPath directory, string stem, AssetReference source, CookedGlb cooked, AssetReference? recorded, AssetReference? skeleton)
        {
            var skinned = cooked.Mesh.Layout == MeshVertexLayout.Skinned;
            var slot = skinned ? MeshSlot.SkinnedMesh : MeshSlot.Mesh;
            var fallback = directory / (stem + MeshReferenceDocument.SuffixOf(slot));

            var target = Target(index, recorded, fallback);
            if (MeshReferenceDocument.SlotOf(target) != slot)
            {
                if (fileSystem.FileExists(target))
                {
                    fileSystem.DeleteFile(target);
                    var meta = SidecarMeta.PathFor(target);
                    if (fileSystem.FileExists(meta)) fileSystem.DeleteFile(meta);
                    _written.Add(new ExtractedFile(index.Relative(target), $"removed: the GLB is {(skinned ? "skinned" : "rigid")} now, so its document is a {MeshReferenceDocument.SuffixOf(slot)}"));
                }

                recorded = null;
                target = fallback;
            }

            if (!skinned) return Document(index, target, new MeshReferenceDocument(source, MeshSlot.Mesh), recorded);

            if (skeleton is null)
            {
                _errors.Add($"{index.Relative(glb)}: has a skin but no node tree to cook a skeleton from, so no skinned mesh document can name one");
                return recorded;
            }

            // The document names the skeleton by GUID, so a skeleton minted a moment ago has to be
            // identified before the document that names it is written.
            if (skeleton.Guid == Guid.Empty)
            {
                index = rescan();
                skeleton = Identified(index, skeleton);
            }

            return Document(index, target, new MeshReferenceDocument(source, MeshSlot.SkinnedMesh, Skeleton: skeleton), recorded);
        }

        /// <summary>Where a recorded entry's document is NOW, by identity — a moved file is re-synced in place, not abandoned for a fresh one beside the GLB; <paramref name="fallback"/> when nothing carries the guid.</summary>
        private static UPath Target(AssetIndex index, AssetReference? recorded, UPath fallback)
        {
            if (recorded is null) return fallback;
            var resolution = index.Resolve(recorded);
            return resolution.Status is ReferenceStatus.Resolved or ReferenceStatus.Stale ? resolution.Asset : fallback;
        }

        /// <summary>
        /// Each clip the GLB has now, paired with the document that already stands for it: by
        /// name when the GLB has that name once, else by the hash the document recorded (the DCC
        /// renamed it), else by index (the DCC renamed AND edited it). What pairs with nothing
        /// gets a new document; a reorder or rename updates the one it has, under its guid.
        /// </summary>
        private List<GlbExtraction.NamedReference> Clips(AssetIndex index, UPath directory, string stem, AssetReference source, CookedGlb cooked, GlbExtraction recorded)
        {
            var previous = recorded.Clips
                .Select(clip => (clip, Path: Target(index, clip.Reference, UPath.Empty), Existing: (MeshReferenceDocument?)null))
                .Where(pair => pair.Path != UPath.Empty && fileSystem.FileExists(pair.Path))
                .Select(pair =>
                {
                    try { return pair with { Existing = MeshReferenceDocument.Load(fileSystem, pair.Path) }; }
                    catch (FormatException) { return pair; }
                })
                .ToList();

            var hashes = cooked.Clips.Select(GltfCook.ClipFingerprint).ToList();
            var unpaired = previous.ToList();
            var pairing = new (UPath Path, string? Reason, AssetReference Recorded)?[cooked.Clips.Count];
            for (var i = 0; i < cooked.Clips.Count; i++)
            {
                var name = cooked.Clips[i].Name;
                if (cooked.Clips.Count(c => c.Name == name) != 1) continue;
                var byName = unpaired.FindIndex(p => p.clip.Name == name);
                if (byName < 0) continue;
                pairing[i] = (unpaired[byName].Path, unpaired[byName].clip.Index == i ? null : "reordered in the GLB", unpaired[byName].clip.Reference);
                unpaired.RemoveAt(byName);
            }

            for (var i = 0; i < cooked.Clips.Count; i++)
            {
                if (pairing[i] is not null) continue;
                var byHash = unpaired.FindIndex(p => p.Existing?.Hash == hashes[i]);
                if (byHash < 0) continue;
                pairing[i] = (unpaired[byHash].Path, $"renamed in the GLB from '{unpaired[byHash].clip.Name}'", unpaired[byHash].clip.Reference);
                unpaired.RemoveAt(byHash);
            }

            for (var i = 0; i < cooked.Clips.Count; i++)
            {
                if (pairing[i] is not null) continue;
                var byIndex = unpaired.FindIndex(p => p.clip.Index == i);
                if (byIndex < 0) continue;
                pairing[i] = (unpaired[byIndex].Path, $"renamed and re-exported in the GLB from '{unpaired[byIndex].clip.Name}'", unpaired[byIndex].clip.Reference);
                unpaired.RemoveAt(byIndex);
            }

            var names = new UniqueNames();
            var result = new List<GlbExtraction.NamedReference>();
            for (var i = 0; i < cooked.Clips.Count; i++)
            {
                var clip = cooked.Clips[i];
                var name = names.Mint(clip.Name, i);
                var path = pairing[i]?.Path ?? directory / $"{stem}.{name}.anim";
                var wanted = new MeshReferenceDocument(source, MeshSlot.Clip, clip.Name, i, hashes[i]);
                if (Document(index, path, wanted, pairing[i]?.Recorded, pairing[i]?.Reason) is { } reference)
                {
                    result.Add(new GlbExtraction.NamedReference(i, name, reference));
                }
            }

            return result;
        }

        /// <summary>
        /// A mesh reference document is tool-owned: written when missing, rewritten when the GLB
        /// changed what it names (a clip renamed or reordered), left alone when it already says
        /// so. One that names ANOTHER GLB, or does not parse, is not this GLB's to overwrite —
        /// refused until the author deletes it or passes <c>--take-glb</c>; the record keeps
        /// naming what it named, so the file is still followed and the GLB still reads as minted.
        /// </summary>
        private AssetReference? Document(AssetIndex index, UPath path, MeshReferenceDocument wanted, AssetReference? recorded, string? why = null)
        {
            var relative = index.Relative(path);
            if (!fileSystem.FileExists(path))
            {
                Write(index, path, wanted.WriteBytes());
                return Reference(index, path);
            }

            MeshReferenceDocument? existing;
            try
            {
                existing = MeshReferenceDocument.Load(fileSystem, path);
            }
            catch (FormatException)
            {
                existing = null;
            }

            if (existing is not null && existing.Source.Guid == wanted.Source.Guid && existing.Slot == wanted.Slot)
            {
                if (existing != wanted)
                {
                    fileSystem.WriteAllBytes(path, wanted.WriteBytes());
                    _written.Add(new ExtractedFile(relative, why ?? "updated: the GLB changed what it names"));
                }
                else
                {
                    _kept.Add(relative);
                }

                return Reference(index, path);
            }

            if (resolution == ConflictResolution.TakeGlb)
            {
                fileSystem.WriteAllBytes(path, wanted.WriteBytes());
                _written.Add(new ExtractedFile(relative, "existed and was not this GLB's: took the GLB's"));
                return Reference(index, path);
            }

            _errors.Add(existing is null
                ? $"{relative}: exists and is not a readable mesh reference; delete it, or re-run with `--take-glb` to overwrite it"
                : $"{relative}: names '{existing.Source.Path}', not this GLB; delete it, or re-run with `--take-glb` to overwrite it");
            return recorded;
        }

        /// <summary>One extracted file under the sync rule (an image today): the GLB side is what it extracts to now, the document side is the file on disk. <see langword="null"/> when a foreign file was refused.</summary>
        private GlbExtraction.Entry? Blob(AssetIndex index, UPath path, byte[] fresh, GlbExtraction.Entry? recorded, string kind)
        {
            var glbSide = Fingerprint(fresh);
            var exists = fileSystem.FileExists(path);
            var documentSide = exists ? Fingerprint(fileSystem.ReadAllBytes(path)) : null;
            var relative = index.Relative(path);

            if (!exists)
            {
                Write(index, path, fresh);
                return new GlbExtraction.Entry(Reference(index, path), glbSide, glbSide);
            }

            if (recorded is null) return Foreign(relative, Reference(index, path), glbSide, documentSide!, () => fileSystem.WriteAllBytes(path, fresh));

            var glbChanged = recorded.GlbFingerprint != glbSide;
            var documentChanged = recorded.DocumentFingerprint != documentSide;
            switch (glbChanged, documentChanged)
            {
                case (false, false):
                    return recorded;

                case (true, false):
                    fileSystem.WriteAllBytes(path, fresh);
                    _written.Add(new ExtractedFile(relative, "re-extracted: the GLB changed"));
                    return recorded with { GlbFingerprint = glbSide, DocumentFingerprint = glbSide };

                case (false, true):
                    // Nothing produces an edited blob today; the direction is reserved, not silent.
                    // The record keeps the LAST-SYNCED fingerprint, so the divergence stays visible
                    // and a later re-export is the conflict it is, not a silent overwrite.
                    _warnings.Add($"{relative} changed since it was extracted, and a {kind} cannot be written back into the GLB yet; `extract --take-glb` re-extracts it, or keep the edit and this warning");
                    return recorded;

                default:
                    return Conflict(relative, recorded, path, fresh, glbSide, documentSide!);
            }
        }

        /// <summary>
        /// A file at the extraction path that no sync recorded: another GLB's output, or the author's.
        /// Adopted when it already holds what the GLB extracts to; otherwise the flags name a side,
        /// and without one it is refused rather than recorded — recording it would make it this
        /// GLB's on the next re-export, and for an image bind the GLB to pixels that are not its own.
        /// </summary>
        private GlbExtraction.Entry? Foreign(string relative, AssetReference reference, string glbSide, string documentSide, Action takeGlb)
        {
            if (glbSide == documentSide)
            {
                _kept.Add($"{relative} (exists with what the GLB extracts to; adopted)");
                return new GlbExtraction.Entry(reference, glbSide, glbSide);
            }

            switch (resolution)
            {
                case ConflictResolution.TakeGlb:
                    takeGlb();
                    _written.Add(new ExtractedFile(relative, "existed and was not extracted by this tool: took the GLB's"));
                    return new GlbExtraction.Entry(reference, glbSide, glbSide);

                case ConflictResolution.TakeDocument:
                    _kept.Add($"{relative} (existed and was not extracted by this tool: adopted as is)");
                    return new GlbExtraction.Entry(reference, glbSide, documentSide);

                default:
                    _errors.Add($"{relative}: exists and was not extracted by this tool, and differs from what the GLB extracts to; delete it, or re-run with `--take-glb` to overwrite it or `--take-document` to adopt it");
                    return null;
            }
        }

        private GlbExtraction.Entry Conflict(string relative, GlbExtraction.Entry recorded, UPath path, byte[] fresh, string glbSide, string documentSide)
        {
            switch (resolution)
            {
                case ConflictResolution.TakeGlb:
                    fileSystem.WriteAllBytes(path, fresh);
                    _written.Add(new ExtractedFile(relative, "conflict: took the GLB's"));
                    return recorded with { GlbFingerprint = glbSide, DocumentFingerprint = glbSide };

                case ConflictResolution.TakeDocument:
                    _written.Add(new ExtractedFile(relative, "conflict: kept the document's"));
                    return recorded with { GlbFingerprint = glbSide, DocumentFingerprint = documentSide };

                default:
                    _errors.Add($"{relative}: both the GLB and the extracted file changed since they were last in step; re-run with `--take-glb` or `--take-document`");
                    return recorded;
            }
        }

        /// <summary>A material document per glTF material, its texture bindings resolved to identities through the GLB's own image references.</summary>
        private List<GlbExtraction.NamedEntry> Materials(AssetIndex index, UPath directory, string stem, byte[] bytes, GltfAsset asset, GlbExtraction recorded)
        {
            var relativeGlb = index.Relative(glb);
            var imagePaths = MeshContainer.Read(glb, bytes)
                .Select(named => (Slot: named.Slot, Path: MeshContainer.AssetPathFor(relativeGlb, named.Uri)))
                .ToDictionary(pair => pair.Slot, pair => pair.Path, StringComparer.Ordinal);

            AssetReference? TextureAt(int imageIndex)
            {
                if (imageIndex < 0) return null;
                if (!imagePaths.TryGetValue($"images[{imageIndex}]", out var path) || path is null) return null;
                return index.IdentityOf(index.Root / path) is { } guid ? new AssetReference(guid, path) : null;
            }

            var result = new List<(int, string, GlbExtraction.Entry?)>();
            var names = new UniqueNames();
            for (var i = 0; i < asset.Materials.Length; i++)
            {
                var material = asset.Materials[i];
                var name = names.Mint(material.Name ?? $"material_{i}", i);
                var previous = recorded.Materials.FirstOrDefault(m => m.Index == i)?.Entry;
                var path = Target(index, previous?.Reference, directory / $"{stem}.{name}.material");
                var document = MaterialDocumentFrom(material, name, TextureAt, out var unresolved);
                foreach (var missing in unresolved) _warnings.Add($"{index.Relative(path)}: {missing} names an image with no identity; the slot was left empty");

                result.Add((i, name, Material(index, path, i, document, previous)));
            }

            return Named(result);
        }

        /// <summary>
        /// A material under the sync rule, both ways: the GLB side is the document the GLB would
        /// extract to now, the document side is the file — both fingerprinted over the
        /// glTF-expressible subset, so a Paradise-only edit is never a divergence. An edited
        /// document is written back into the GLB's material; a re-exported material is re-extracted
        /// (keeping the document's Paradise-only fields); both changed is a conflict.
        /// </summary>
        private GlbExtraction.Entry? Material(AssetIndex index, UPath path, int materialIndex, CanonicalTomlTable fromGlb, GlbExtraction.Entry? recorded)
        {
            var relative = index.Relative(path);
            var glbSide = Fingerprint(CanonicalTomlWriter.WriteBytes(GlbMaterialWriter.Subset(fromGlb)));

            if (!fileSystem.FileExists(path))
            {
                Write(index, path, CanonicalTomlWriter.WriteBytes(fromGlb));
                return new GlbExtraction.Entry(Reference(index, path), glbSide, glbSide);
            }

            CanonicalTomlTable onDisk;
            try
            {
                onDisk = MaterialDocument.Load(fileSystem, path);
            }
            catch (FormatException error)
            {
                _warnings.Add($"{relative}: {error.Message}; left alone");
                return recorded ?? new GlbExtraction.Entry(Reference(index, path), glbSide, "");
            }

            var documentSide = Fingerprint(CanonicalTomlWriter.WriteBytes(GlbMaterialWriter.Subset(onDisk)));
            if (recorded is null) return Foreign(relative, Reference(index, path), glbSide, documentSide, () => fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(fromGlb)));

            var glbChanged = recorded.GlbFingerprint != glbSide;
            var documentChanged = recorded.DocumentFingerprint != documentSide;
            switch (glbChanged, documentChanged)
            {
                case (false, false):
                    return recorded;

                case (true, false):
                    return TakeGlb(index, path, fromGlb, onDisk, recorded, glbSide, "re-extracted: the GLB changed");

                case (false, true):
                    return TakeDocument(index, path, materialIndex, onDisk, recorded, documentSide, "written back into the GLB");

                default:
                    return resolution switch
                    {
                        ConflictResolution.TakeGlb => TakeGlb(index, path, fromGlb, onDisk, recorded, glbSide, "conflict: took the GLB's"),
                        ConflictResolution.TakeDocument => TakeDocument(index, path, materialIndex, onDisk, recorded, documentSide, "conflict: kept the document's, written into the GLB"),
                        _ => Refuse(relative, recorded),
                    };
            }
        }

        /// <summary>The GLB's values over the document's, keeping every field glTF cannot express.</summary>
        private GlbExtraction.Entry TakeGlb(AssetIndex index, UPath path, CanonicalTomlTable fromGlb, CanonicalTomlTable onDisk, GlbExtraction.Entry recorded, string glbSide, string why)
        {
            var merged = new CanonicalTomlTable();
            foreach (var (key, value) in fromGlb) merged.Add(key, value);
            foreach (var (key, value) in onDisk)
            {
                if (!merged.ContainsKey(key)) merged.Add(key, value);
            }

            fileSystem.WriteAllBytes(path, CanonicalTomlWriter.WriteBytes(merged));
            _written.Add(new ExtractedFile(index.Relative(path), why));
            return recorded with { GlbFingerprint = glbSide, DocumentFingerprint = glbSide };
        }

        /// <summary>The document's expressible half into the GLB's material; the GLB side then reads as the document.</summary>
        private GlbExtraction.Entry TakeDocument(AssetIndex index, UPath path, int materialIndex, CanonicalTomlTable onDisk, GlbExtraction.Entry recorded, string documentSide, string why)
        {
            var bytes = fileSystem.ReadAllBytes(glb);
            var rewritten = GlbMaterialWriter.Write(bytes, index.Relative(glb), materialIndex, onDisk, out var problem);
            if (problem is not null)
            {
                _errors.Add($"{index.Relative(path)}: {problem}");
                return recorded;
            }

            if (!ReferenceEquals(rewritten, bytes))
            {
                fileSystem.WriteAllBytes(glb, rewritten);
                _written.Add(new ExtractedFile(index.Relative(glb), $"material '{path.GetName()}' {why}"));
            }

            return recorded with { GlbFingerprint = documentSide, DocumentFingerprint = documentSide };
        }

        private GlbExtraction.Entry Refuse(string relative, GlbExtraction.Entry recorded)
        {
            _errors.Add($"{relative}: both the GLB's material and the document changed since they were last in step; re-run with `--take-glb` or `--take-document`");
            return recorded;
        }

        private static CanonicalTomlTable MaterialDocumentFrom(GltfMaterialData material, string name, Func<int, AssetReference?> textureAt, out List<string> unresolved)
        {
            unresolved = [];
            var table = new CanonicalTomlTable
            {
                { "Name", name },
                { "MetallicFactor", Widen(material.MetallicFactor) },
                { "RoughnessFactor", Widen(material.RoughnessFactor) },
                { "NormalScale", Widen(material.NormalScale) },
                { "OcclusionStrength", Widen(material.OcclusionStrength) },
                { "AlphaMode", material.AlphaMode.ToString() },
                { "AlphaCutoff", Widen(material.AlphaCutoff) },
                { "DoubleSided", material.DoubleSided },
                { "TransmissionFactor", Widen(material.TransmissionFactor) },
                { "BaseColorUvOffset", new List<object> { Widen(material.BaseColorUvTransform.Offset.X), Widen(material.BaseColorUvTransform.Offset.Y) } },
                { "BaseColorUvScale", new List<object> { Widen(material.BaseColorUvTransform.Scale.X), Widen(material.BaseColorUvTransform.Scale.Y) } },
                { "BaseColorUvRotation", Widen(material.BaseColorUvTransform.Rotation) },
            };

            foreach (var (key, image) in new[]
            {
                ("BaseColorTexture", material.BaseColorImage),
                ("MetallicRoughnessTexture", material.MetallicRoughnessImage),
                ("NormalTexture", material.NormalImage),
                ("OcclusionTexture", material.OcclusionImage),
                ("EmissiveTexture", material.EmissiveImage),
            })
            {
                var reference = textureAt(image);
                if (image >= 0 && reference is null) unresolved.Add(key);
                table.Add(key, AssetReferenceCodec.Write(reference));
            }

            table.Add("BaseColorFactor", Colour(material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z, material.BaseColorFactor.W));
            table.Add("EmissiveFactor", Colour(material.EmissiveFactor.X, material.EmissiveFactor.Y, material.EmissiveFactor.Z, 1f));
            return table;
        }

        private static CanonicalTomlTable Colour(float r, float g, float b, float a)
            => new() { { "r", Widen(r) }, { "g", Widen(g) }, { "b", Widen(b) }, { "a", Widen(a) } };

        /// <summary>A GLB float as the document's double: the float's shortest round-trip form, so <c>0.1f</c> is written <c>0.1</c> and not <c>0.10000000149011612</c>, and a hand-typed <c>0.1</c> fingerprints the same.</summary>
        private static double Widen(float value) => double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The prefab wiring the extracted assets: written once, the author's from then on.</summary>
        private AssetReference? Prefab(AssetIndex index, AssetProjectLayout layout, UPath path, string stem, Guid glbGuid, GlbExtraction extraction, CookedGlb cooked, GltfAsset asset)
        {
            if (fileSystem.FileExists(path))
            {
                _kept.Add(index.Relative(path));
                return extraction.Prefab ?? Reference(index, path);
            }

            var document = new PrefabDocument();
            var root = PrefabObject.WithMeta(Guid.NewGuid(), stem);
            root.Meta!.Data.Add(GlbExtraction.GeneratedFrom, DocumentGuid.Format(glbGuid));

            var skinned = cooked.Mesh.Layout == MeshVertexLayout.Skinned;
            if (MeshComponent(layout, skinned) is { } component)
            {
                root.Components.Add(new PrefabComponent(component.Id, component.Type, new CanonicalTomlTable
                {
                    { component.Field, AssetReferenceCodec.Write(extraction.Mesh!) },
                }));
            }
            else
            {
                _warnings.Add($"{index.Relative(path)}: the game's schema names no mesh-bearing component, so the prefab carries no mesh; add one by hand");
            }

            var slots = new List<object>();
            foreach (var materialIndex in cooked.SlotMaterials)
            {
                var entry = materialIndex < 0 ? null : extraction.Materials.FirstOrDefault(m => m.Index == materialIndex)?.Entry;
                slots.Add(AssetReferenceCodec.Write(entry?.Reference));
            }

            root.Components.Add(new PrefabComponent(GlbExtraction.MaterialsComponentId, GlbExtraction.MaterialsComponentType, new CanonicalTomlTable { { "Slots", slots } }));
            document.Objects.Add(root);
            PrefabDocumentSerializer.Save(fileSystem, path, document);
            _written.Add(new ExtractedFile(index.Relative(path)));
            _minted.Add(path);
            return new AssetReference(Guid.Empty, index.Relative(path));
        }

        /// <summary>The component the game authors a mesh into: the manifest's choice, else the schema's — a component with a mesh-authored field, "Skinned" in its name deciding the rigged one.</summary>
        private (Guid Id, string Type, string Field)? MeshComponent(AssetProjectLayout layout, bool skinned)
        {
            var schemaPath = layout.Editor / "authoring-schema.json";
            if (!fileSystem.FileExists(schemaPath))
            {
                _warnings.Add("no .editor/authoring-schema.json: build the game's launcher once so the prefab can name its mesh component");
                return null;
            }

            AuthoringSchemaDocument schema;
            try
            {
                schema = AuthoringSchemaReader.Read(fileSystem.ReadAllText(schemaPath));
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or System.Text.Json.JsonException)
            {
                _warnings.Add($"the authoring schema could not be read ({error.Message}); the prefab carries no mesh component");
                return null;
            }

            var candidates = schema.Components
                .SelectMany(component => component.Fields
                    .Where(field => field.AuthoredBy == AuthoredBySources.Mesh)
                    .Select(field => (component.Id, component.Type, field.Name)))
                .ToList();
            if (candidates.Count == 0) return null;

            var wanted = skinned ? _extract.SkinnedMeshComponent : _extract.StaticMeshComponent;
            if (wanted is not null)
            {
                var named = candidates.FirstOrDefault(c => string.Equals(c.Type, wanted, StringComparison.Ordinal) || c.Type.EndsWith("." + wanted, StringComparison.Ordinal));
                if (named != default) return named;
                _warnings.Add($"project.toml names mesh component '{wanted}', which the schema does not have; choosing by name instead");
            }

            var rigged = candidates.Where(c => c.Type.Contains("Skinned", StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = skinned
                ? rigged.FirstOrDefault(candidates[0])
                : candidates.FirstOrDefault(c => !rigged.Contains(c), candidates[0]);
            return chosen;
        }

        private void Write(AssetIndex index, UPath path, byte[] bytes)
        {
            fileSystem.CreateDirectory(path.GetDirectory());
            fileSystem.WriteAllBytes(path, bytes);
            _written.Add(new ExtractedFile(index.Relative(path)));
            _minted.Add(path);
        }

        /// <summary>The entries that resolved; a refused one has already been reported and is not recorded.</summary>
        private static List<GlbExtraction.NamedEntry> Named(IEnumerable<(int Index, string Name, GlbExtraction.Entry? Entry)> entries)
            => entries.Where(e => e.Entry is not null).Select(e => new GlbExtraction.NamedEntry(e.Index, e.Name, e.Entry!)).ToList();

        private static AssetReference Reference(AssetIndex index, UPath path)
            => new(index.IdentityOf(path) ?? Guid.Empty, index.Relative(path));

        private static GlbExtraction Identified(AssetIndex index, GlbExtraction extraction) => extraction with
        {
            Mesh = extraction.Mesh is null ? null : Identified(index, extraction.Mesh),
            Skeleton = extraction.Skeleton is null ? null : Identified(index, extraction.Skeleton),
            Prefab = extraction.Prefab is null ? null : Identified(index, extraction.Prefab),
            Clips = extraction.Clips.Select(clip => clip with { Reference = Identified(index, clip.Reference) }).ToList(),
            Materials = extraction.Materials.Select(material => material with { Entry = Identified(index, material.Entry) }).ToList(),
            Images = extraction.Images.Select(image => image with { Entry = Identified(index, image.Entry) }).ToList(),
        };

        private static GlbExtraction.Entry Identified(AssetIndex index, GlbExtraction.Entry entry)
            => entry with { Reference = Identified(index, entry.Reference) };

        private static AssetReference Identified(AssetIndex index, AssetReference reference)
            => Reference(index, index.Root / reference.Path);

        private static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        // Windows' set, applied everywhere, so the same GLB extracts to the same file names on
        // every platform; Path.GetInvalidFileNameChars is only '/' and NUL on Unix.
        private static readonly char[] s_unsafe = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', ' '];

        private static string FileSafe(string name)
        {
            var chars = name.Select(c => s_unsafe.Contains(c) || c < ' ' ? '_' : c).ToArray();
            return new string(chars);
        }

        /// <summary>File stems for one kind within one run: glTF names are optional and need not be unique, and file-safing merges more, so a collision gets its glTF index appended.</summary>
        private sealed class UniqueNames
        {
            private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

            public string Mint(string name, int index)
            {
                var safe = FileSafe(name);
                if (safe.Length == 0) safe = $"_{index}";
                var unique = _used.Add(safe) ? safe : $"{safe}_{index}";
                _used.Add(unique);
                return unique;
            }
        }

        private ExtractResult Fail(string error)
        {
            _errors.Add(error);
            return Finish();
        }

        private bool _hasAuthoredParts;

        private ExtractResult Finish() => new(_errors.Count == 0, _errors, _written, _kept, _warnings, _hasAuthoredParts);
    }
}

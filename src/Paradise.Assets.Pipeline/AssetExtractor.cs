using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Animation;
using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one <c>extract</c> did: the authored files it wrote, the ones it left because they were the author's, and what stopped it.</summary>
public sealed record ExtractResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Warnings);

/// <summary>Which side wins when both the GLB and an extracted document changed since their last sync; the default is to refuse and say so.</summary>
public enum ConflictResolution
{
    Refuse,
    TakeGlb,
    TakeDocument,
}

/// <summary>
/// The <c>extract</c> verb: what a GLB embeds becomes authored assets beside it — the mesh blob,
/// the skeleton and clips, a material document per glTF material, the embedded textures as
/// files — and a prefab that wires them together. The GLB is then a pure interchange container:
/// everything downstream references the extracted assets, and the build never opens a GLB.
/// </summary>
/// <remarks>
/// <para>
/// Explicit, never a build side effect: extraction mints identities and creates files an author
/// edits, and a build doing that per save would edit committed sidecars under them. Idempotent:
/// a file this verb did not create is never overwritten, and a second run reports what it kept.
/// </para>
/// <para>
/// Each extracted entry is recorded in the GLB's sidecar with the document it maps to and a
/// FINGERPRINT of each side as of the last sync — the GLB side is the hash of what the GLB
/// would extract to now, the document side the hash of the file's parsed values — so the next
/// run can tell "the GLB was re-exported" from "the author edited the document": the first
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
        bool generatePrefab = true)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        glb.AssertAbsolute(nameof(glb));

        var chain = importers ?? AssetImporters.All;
        var log = logger ?? NullLogger.Instance;
        var run = new Run(fileSystem, layout, glb, chain, resolution, log, generatePrefab);
        return run.Execute();
    }

    private sealed class Run(IFileSystem fileSystem, AssetProjectLayout layout, UPath glb, IReadOnlyList<IAssetImporter> chain, ConflictResolution resolution, ILogger log, bool generatePrefab)
    {
        private readonly List<string> _errors = [];
        private readonly List<string> _written = [];
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
            // extracted textures by guid, and the prefab names everything by guid.
            var maintainer = new SidecarMaintainer(fileSystem, layout, log, ignore: manifest.Ignore, importers: chain);
            AssetIndex Rescan()
            {
                foreach (var path in _minted) maintainer.Ensure(path);
                return AssetIndex.Scan(fileSystem, layout.Assets, manifest.Ignore);
            }

            var bytes = fileSystem.ReadAllBytes(glb);
            bytes = Textures(index, directory, stem, bytes);
            if (_errors.Count > 0) return Finish();
            index = Rescan();

            GltfAsset asset;
            CookedGlb cooked;
            try
            {
                asset = GltfSceneReader.ReadGeometry(bytes);
                cooked = GltfCook.Cook(asset);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                return Fail($"{index.Relative(glb)}: {error.Message}");
            }

            var recorded = settings;
            var mesh = Blob(index, directory / $"{stem}.mesh", MeshBlobFormat.Write(cooked.Mesh), recorded.Mesh, "mesh");
            var skeleton = cooked.Skeleton is { } rig ? Blob(index, directory / $"{stem}.skeleton", SkeletonFormat.Write(rig), recorded.Skeleton, "skeleton") : null;
            var clips = cooked.Clips
                .Select(clip => new GlbExtraction.NamedEntry(clip.Name, Blob(index, directory / $"{stem}.{FileSafe(clip.Name)}.anim", ClipFormat.Write(clip), recorded.Clips.FirstOrDefault(c => c.Name == clip.Name)?.Entry, "clip")))
                .ToList();
            var materials = Materials(index, directory, stem, bytes, asset, recorded);
            if (_errors.Count > 0) return Finish();

            index = Rescan();

            var extraction = new GlbExtraction(
                settings.Directory,
                Identified(index, mesh),
                skeleton is null ? null : Identified(index, skeleton),
                clips.Select(clip => clip with { Entry = Identified(index, clip.Entry) }).ToList(),
                materials.Select(material => material with { Entry = Identified(index, material.Entry) }).ToList(),
                recorded.Prefab);

            var prefabPath = directory / $"{stem}.prefab";
            if (generatePrefab) extraction = extraction with { Prefab = Prefab(index, layout, prefabPath, stem, meta.Guid, extraction, cooked, asset) };
            if (extraction.Prefab is { } prefab && prefab.Reference.Guid == Guid.Empty)
            {
                index = Rescan();
                extraction = extraction with { Prefab = Identified(index, prefab) };
            }

            meta = SidecarMeta.Load(fileSystem, sidecarPath);
            GlbImportSettings.WriteExtraction(meta, extraction);
            meta.Save(fileSystem, sidecarPath);

            // The GLB's own image references, by identity: an image that just left the container
            // is an external uri now, and the sidecar records it like any other.
            MeshReferences.Apply(fileSystem, glb, MeshReferences.Reconcile(fileSystem, index, glb), rewriteContainer: false);
            return Finish();
        }

        private UPath Directory(AssetIndex index, GlbExtraction settings, ProjectManifest manifest)
        {
            var relative = settings.Directory ?? manifest.Extract.Directory;
            return relative is null ? glb.GetDirectory() : (layout.Assets / relative).ToAbsolute();
        }

        /// <summary>Embedded images become files beside the GLB and the GLB points at them: the file IS the texture now, and the DCC re-imports it as such.</summary>
        private byte[] Textures(AssetIndex index, UPath directory, string stem, byte[] bytes)
        {
            if (!GlbTextureRewriter.TryListEmbedded(bytes, stem, out var embedded, out var problem))
            {
                _errors.Add($"{index.Relative(glb)}: {problem}");
                return bytes;
            }

            if (embedded.Count == 0) return bytes;

            var uris = new Dictionary<int, string>();
            foreach (var image in embedded)
            {
                var extension = image.SourceExtension ?? ".ktx2";
                var path = directory / $"{stem}_{image.Index}{extension}";
                if (!fileSystem.FileExists(path)) Write(index, path, image.Bytes);
                else _kept.Add(index.Relative(path));
                uris[image.Index] = MeshContainer.UriFor(index.Relative(glb), index.Relative(path));
            }

            if (!GlbTextureRewriter.TryExternalizeSources(bytes, embedded, uris, out var rewritten, out var error))
            {
                _errors.Add($"{index.Relative(glb)}: {error}");
                return bytes;
            }

            fileSystem.WriteAllBytes(glb, rewritten);
            _written.Add(index.Relative(glb) + " (images now external)");
            return rewritten;
        }

        /// <summary>One extracted blob under the sync rule: the GLB side is what it extracts to now, the document side is the file on disk.</summary>
        private GlbExtraction.Entry Blob(AssetIndex index, UPath path, byte[] fresh, GlbExtraction.Entry? recorded, string kind)
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

            if (recorded is null)
            {
                _kept.Add($"{relative} (exists and was not extracted by this tool; delete it to re-extract)");
                return new GlbExtraction.Entry(Reference(index, path), glbSide, documentSide!);
            }

            var glbChanged = recorded.GlbFingerprint != glbSide;
            var documentChanged = recorded.DocumentFingerprint != documentSide;
            switch (glbChanged, documentChanged)
            {
                case (false, false):
                    return recorded;

                case (true, false):
                    fileSystem.WriteAllBytes(path, fresh);
                    _written.Add($"{relative} (re-extracted: the GLB changed)");
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

        private GlbExtraction.Entry Conflict(string relative, GlbExtraction.Entry recorded, UPath path, byte[] fresh, string glbSide, string documentSide)
        {
            switch (resolution)
            {
                case ConflictResolution.TakeGlb:
                    fileSystem.WriteAllBytes(path, fresh);
                    _written.Add($"{relative} (conflict: took the GLB's)");
                    return recorded with { GlbFingerprint = glbSide, DocumentFingerprint = glbSide };

                case ConflictResolution.TakeDocument:
                    _written.Add($"{relative} (conflict: kept the document's)");
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

            var result = new List<GlbExtraction.NamedEntry>();
            for (var i = 0; i < asset.Materials.Length; i++)
            {
                var material = asset.Materials[i];
                var name = FileSafe(material.Name ?? $"material_{i}");
                var path = directory / $"{stem}.{name}.material";
                var document = MaterialDocumentFrom(material, name, TextureAt, out var unresolved);
                foreach (var missing in unresolved) _warnings.Add($"{index.Relative(path)}: {missing} names an image with no identity; the slot was left empty");

                var fresh = CanonicalTomlWriter.WriteBytes(document);
                var previous = recorded.Materials.FirstOrDefault(m => m.Name == name)?.Entry;
                result.Add(new GlbExtraction.NamedEntry(name, Blob(index, path, fresh, previous, "material")));
            }

            return result;
        }

        private static CanonicalTomlTable MaterialDocumentFrom(GltfMaterialData material, string name, Func<int, AssetReference?> textureAt, out List<string> unresolved)
        {
            unresolved = [];
            var table = new CanonicalTomlTable
            {
                { "Name", name },
                { "MetallicFactor", (double)material.MetallicFactor },
                { "RoughnessFactor", (double)material.RoughnessFactor },
                { "NormalScale", (double)material.NormalScale },
                { "OcclusionStrength", (double)material.OcclusionStrength },
                { "AlphaMode", material.AlphaMode.ToString() },
                { "AlphaCutoff", (double)material.AlphaCutoff },
                { "DoubleSided", material.DoubleSided },
                { "TransmissionFactor", (double)material.TransmissionFactor },
                { "BaseColorUvOffset", new List<object> { (double)material.BaseColorUvTransform.Offset.X, (double)material.BaseColorUvTransform.Offset.Y } },
                { "BaseColorUvScale", new List<object> { (double)material.BaseColorUvTransform.Scale.X, (double)material.BaseColorUvTransform.Scale.Y } },
                { "BaseColorUvRotation", (double)material.BaseColorUvTransform.Rotation },
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
            => new() { { "r", (double)r }, { "g", (double)g }, { "b", (double)b }, { "a", (double)a } };

        /// <summary>The prefab wiring the extracted assets: written once, the author's from then on.</summary>
        private GlbExtraction.Entry? Prefab(AssetIndex index, AssetProjectLayout layout, UPath path, string stem, Guid glbGuid, GlbExtraction extraction, CookedGlb cooked, GltfAsset asset)
        {
            if (fileSystem.FileExists(path))
            {
                _kept.Add(index.Relative(path));
                return extraction.Prefab ?? new GlbExtraction.Entry(Reference(index, path), "", "");
            }

            var document = new PrefabDocument();
            var root = PrefabObject.WithMeta(Guid.NewGuid(), stem);
            root.Meta!.Data.Add(GlbExtraction.GeneratedFrom, DocumentGuid.Format(glbGuid));

            var skinned = cooked.Mesh.Layout == MeshVertexLayout.Skinned;
            if (MeshComponent(layout, skinned) is { } component)
            {
                root.Components.Add(new PrefabComponent(component.Id, component.Type, new CanonicalTomlTable
                {
                    { component.Field, AssetReferenceCodec.Write(extraction.Mesh!.Reference) },
                }));
            }
            else
            {
                _warnings.Add($"{index.Relative(path)}: the game's schema names no mesh-bearing component, so the prefab carries no mesh; add one by hand");
            }

            var slots = new List<object>();
            foreach (var materialIndex in cooked.SlotMaterials)
            {
                var name = materialIndex >= 0 ? FileSafe(asset.Materials[materialIndex].Name ?? $"material_{materialIndex}") : null;
                var entry = name is null ? null : extraction.Materials.FirstOrDefault(m => m.Name == name)?.Entry;
                slots.Add(AssetReferenceCodec.Write(entry?.Reference));
            }

            root.Components.Add(new PrefabComponent(GlbExtraction.MaterialsComponentId, GlbExtraction.MaterialsComponentType, new CanonicalTomlTable { { "Slots", slots } }));
            document.Objects.Add(root);
            PrefabDocumentSerializer.Save(fileSystem, path, document);
            _written.Add(index.Relative(path));
            _minted.Add(path);
            return new GlbExtraction.Entry(new AssetReference(Guid.Empty, index.Relative(path)), "", "");
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
            _written.Add(index.Relative(path));
            _minted.Add(path);
        }

        private static AssetReference Reference(AssetIndex index, UPath path)
            => new(index.IdentityOf(path) ?? Guid.Empty, index.Relative(path));

        private static GlbExtraction.Entry Identified(AssetIndex index, GlbExtraction.Entry entry)
            => entry with { Reference = Reference(index, index.Root / entry.Reference.Path) };

        private static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static string FileSafe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == ' ' ? '_' : c).ToArray();
            return new string(chars);
        }

        private ExtractResult Fail(string error)
        {
            _errors.Add(error);
            return Finish();
        }

        private ExtractResult Finish() => new(_errors.Count == 0, _errors, _written, _kept, _warnings);
    }
}

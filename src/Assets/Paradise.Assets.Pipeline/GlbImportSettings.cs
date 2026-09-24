using Paradise.Animation.Offline;
using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>One external file a mesh container names, resolved to an identity.</summary>
/// <param name="Slot">Where in the container the reference sits (<c>images[0]</c>); the key an entry is matched on.</param>
/// <param name="Uri">The uri as the container spells it, relative to itself; recorded so a re-export that changed it can be told from a texture that moved.</param>
/// <param name="Reference">The identity it resolved to, and the assets-relative path that identity lived at.</param>
public readonly record struct MeshReference(string Slot, string Uri, AssetReference Reference);

/// <summary>
/// The <c>[glb]</c> domain: a GLB's external references, resolved to identities and
/// kept in the SIDECAR rather than in the container.
/// </summary>
/// <remarks>
/// A GLB could carry this in <c>extras</c>; an FBX or a USD cannot, and two mechanisms by format
/// is the wrong place to end up. The sidecar is tooling-owned and format-neutral: a per-format
/// reader only has to EXTRACT <c>(slot, uri)</c> pairs, and the pipeline resolves them once and
/// records the answer here — the way Unity's importer records an FBX's texture remaps in its
/// <c>.meta</c>. This is derived data the tooling computes from bytes it cannot author, not a copy
/// of anything authored, which is why it belongs in import settings and a document's reference
/// list does not. It changes only when the container's uris change, so an ordinary edit never
/// dirties it.
/// </remarks>
public sealed class GlbImportSettings : IImportSettingsDomain
{
    public const string Domain = "glb";

    public const string ReferencesKey = "references";

    public const string SlotKey = "slot";

    public const string UriKey = "uri";

    /// <summary><c>optimize = { tolerance = 0.001, distance = 0.1 }</c>: the clip decimation the build applies to this GLB's clips; absent keeps every key.</summary>
    public const string OptimizeKey = "optimize";
    public const string ToleranceKey = "tolerance";
    public const string DistanceKey = "distance";

    public static GlbImportSettings Instance { get; } = new();

    private GlbImportSettings()
    {
    }

    /// <inheritdoc />
    public string Name => Domain;

    /// <inheritdoc />
    public string? Problem(CanonicalTomlTable settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var (key, value) in settings)
        {
            switch (key)
            {
                case OptimizeKey when ReadOptimization(value) is not null: continue;
                case OptimizeKey: return $"holds '{OptimizeKey}' in [{Domain}] that is not {{ tolerance, distance }} with positive numbers";

                // The extraction record moved to [extract]. A sidecar that still carries it here is
                // read once and rewritten by the next extract, so it is not a finding — reporting
                // it would make every GLB in an upgrading project an error before the one command
                // that fixes them all.
                case LegacyExtractKey or LegacyMeshKey or LegacySkeletonKey or LegacyClipsKey or LegacyMaterialsKey or LegacyImagesKey or LegacyPrefabKey: continue;
                case ReferencesKey: break;
                default: return $"holds '{key}' in [{Domain}], which is not a glb setting";
            }

            if (value is not IReadOnlyList<object> entries) return $"holds a non-array '{ReferencesKey}' in [{Domain}]";

            var slots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (ReadEntry(entry) is not { } reference)
                {
                    return $"holds an entry in [{Domain}].{ReferencesKey} that is not {{ slot, uri, guid, path }} with a non-empty UUID";
                }

                if (!slots.Add(reference.Slot))
                {
                    return $"records slot '{reference.Slot}' twice in [{Domain}].{ReferencesKey}; a slot has one identity";
                }
            }
        }

        return null;
    }

    /// <summary>The recorded references, in sidecar order; empty when the domain is absent. Malformed entries are skipped here because <c>verify</c> names them.</summary>
    public static IReadOnlyList<MeshReference> Read(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (meta.Setting(Domain)?.Value(ReferencesKey) is not IReadOnlyList<object> entries) return [];
        var references = new List<MeshReference>();
        foreach (var entry in entries)
        {
            if (ReadEntry(entry) is { } reference) references.Add(reference);
        }

        return references;
    }

    /// <summary>Entries by slot, last wins: a duplicate is verify's finding, not a reason for every other verb to throw.</summary>
    public static Dictionary<string, MeshReference> BySlot(IReadOnlyList<MeshReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var bySlot = new Dictionary<string, MeshReference>(StringComparer.Ordinal);
        foreach (var reference in references) bySlot[reference.Slot] = reference;
        return bySlot;
    }

    /// <summary>Records <paramref name="references"/>, keeping the extraction half of the domain; the domain goes when nothing is left in it.</summary>
    public static void Write(SidecarMeta meta, IReadOnlyList<MeshReference> references)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(references);
        WriteDomain(meta, references, ReadOptimization(meta));
    }

    /// <summary>The recorded clip decimation, or null for none.</summary>
    public static AnimationOptimizer.Setting? ReadOptimization(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return ReadOptimization(meta.Setting(Domain)?.Value(OptimizeKey));
    }

    /// <summary>Records a clip decimation, or removes it with null, keeping the rest of the domain.</summary>
    public static void WriteOptimization(SidecarMeta meta, AnimationOptimizer.Setting? setting)
    {
        ArgumentNullException.ThrowIfNull(meta);
        WriteDomain(meta, Read(meta), setting);
    }

    private static AnimationOptimizer.Setting? ReadOptimization(object? value)
    {
        if (value is not (CanonicalTomlTable or CanonicalInlineTable)) return null;
        if (ReadNumber(Lookup(value, ToleranceKey)) is not { } tolerance || tolerance <= 0f) return null;
        if (ReadNumber(Lookup(value, DistanceKey)) is not { } distance || distance <= 0f) return null;
        return new AnimationOptimizer.Setting(tolerance, distance);
    }

    private static float? ReadNumber(object? value) => value switch
    {
        double number => (float)number,
        float number => number,
        long number => number,
        int number => number,
        _ => null,
    };

    /// <summary>What <c>extract</c> recorded, or an empty record for a GLB never extracted.</summary>
    /// <remarks>The record itself is the engine's and format-neutral (<see cref="ExtractionRecord"/>); this maps it into the shape the GLB pipeline works in.</remarks>
    public static GlbExtraction ReadExtraction(SidecarMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        // A sidecar minted before the record moved out of [glb] is read in its old shape, ONCE:
        // the next extract writes [extract] and WriteDomain drops the legacy keys. Without this the
        // first re-extraction after upgrading loses the per-GLB `extract` directory and every
        // recorded identity, so Target falls back to the default path, writes new files there under
        // NEW guids, and orphans everything the project already references.
        if (meta.Setting(ExtractionRecord.Domain) is null && ReadLegacy(meta) is { } legacy) return legacy;

        return FromRecord(ExtractionRecord.Read(meta));
    }

    /// <summary>The pre-<see cref="ExtractionRecord"/> shape, or null when the sidecar carries none of it. Delete once no tree in the wild predates the move.</summary>
    private static GlbExtraction? ReadLegacy(SidecarMeta meta)
    {
        var table = meta.Setting(Domain);
        if (table is null) return null;

        var directory = table.Value(LegacyExtractKey) as string;
        var mesh = ReadReference(table.Value(LegacyMeshKey));
        var skeleton = ReadReference(table.Value(LegacySkeletonKey));
        var clips = ReadLegacyClips(table.Value(LegacyClipsKey));
        var materials = ReadLegacyNamed(table.Value(LegacyMaterialsKey));
        var images = ReadLegacyNamed(table.Value(LegacyImagesKey));

        if (directory is null && mesh is null && skeleton is null && clips.Count == 0 && materials.Count == 0 && images.Count == 0) return null;
        return new GlbExtraction(directory, mesh, skeleton, clips, materials, images);
    }

    private static List<GlbExtraction.NamedReference> ReadLegacyClips(object? value)
    {
        var result = new List<GlbExtraction.NamedReference>();
        if (value is not IReadOnlyList<object> items) return result;
        foreach (var item in items)
        {
            if (LegacyIndex(item) is { } index && Lookup(item, LegacyNameKey) is string name && ReadReference(item) is { } reference)
            {
                result.Add(new GlbExtraction.NamedReference(index, name, reference));
            }
        }

        return result;
    }

    private static List<GlbExtraction.NamedEntry> ReadLegacyNamed(object? value)
    {
        var result = new List<GlbExtraction.NamedEntry>();
        if (value is not IReadOnlyList<object> items) return result;
        foreach (var item in items)
        {
            if (LegacyIndex(item) is not { } index || Lookup(item, LegacyNameKey) is not string name || ReadReference(item) is not { } reference) continue;
            result.Add(new GlbExtraction.NamedEntry(index, name, new GlbExtraction.Entry(
                reference,
                Lookup(item, LegacyGlbFingerprintKey) as string ?? "",
                Lookup(item, LegacyDocumentFingerprintKey) as string ?? "")));
        }

        return result;
    }

    private static int? LegacyIndex(object? item) => Lookup(item, LegacyIndexKey) switch
    {
        long index and >= 0 and <= int.MaxValue => (int)index,
        int index and >= 0 => index,
        _ => null,
    };

    /// <summary>Generated prefabs stopped being tracked long ago; sidecars minted before that still carry the key.</summary>
    private const string LegacyPrefabKey = "prefab";

    private const string LegacyExtractKey = "extract";
    private const string LegacyMeshKey = "mesh";
    private const string LegacySkeletonKey = "skeleton";
    private const string LegacyClipsKey = "clips";
    private const string LegacyMaterialsKey = "materials";
    private const string LegacyImagesKey = "images";
    private const string LegacyNameKey = "name";
    private const string LegacyIndexKey = "index";
    private const string LegacyGlbFingerprintKey = "glb";
    private const string LegacyDocumentFingerprintKey = "doc";

    /// <summary>The engine's flat parts list as the GLB's named buckets.</summary>
    internal static GlbExtraction FromRecord(Extraction extraction)
    {
        AssetReference? One(string kind) => extraction.OfKind(kind).FirstOrDefault()?.Reference;
        GlbExtraction.Entry Entry(ExtractedPart part) => new(part.Reference, part.SourceFingerprint ?? "", part.DocumentFingerprint ?? "");

        return new GlbExtraction(
            extraction.Directory,
            One(ExtractKind.Meshes),
            One(ExtractKind.Skeletons),
            [.. extraction.OfKind(ExtractKind.Animations).Select(part => new GlbExtraction.NamedReference(part.Index, part.Name, part.Reference))],
            [.. extraction.OfKind(ExtractKind.Materials).Select(part => new GlbExtraction.NamedEntry(part.Index, part.Name, Entry(part)))],
            [.. extraction.OfKind(ExtractKind.Textures).Select(part => new GlbExtraction.NamedEntry(part.Index, part.Name, Entry(part)))]);
    }

    /// <summary>The GLB's named buckets as the engine's flat parts list. A mesh or skeleton has one part per container, so its index is 0 and its name is the file's stem.</summary>
    internal static Extraction ToRecord(GlbExtraction extraction)
    {
        static string Stem(AssetReference reference) => Path.GetFileNameWithoutExtension(reference.Path);
        var parts = new List<ExtractedPart>();

        if (extraction.Mesh is { } mesh) parts.Add(new ExtractedPart(ExtractKind.Meshes, PartOwnership.ToolOwned, 0, Stem(mesh), mesh));
        if (extraction.Skeleton is { } skeleton) parts.Add(new ExtractedPart(ExtractKind.Skeletons, PartOwnership.ToolOwned, 0, Stem(skeleton), skeleton));
        parts.AddRange(extraction.Clips.Select(clip => new ExtractedPart(ExtractKind.Animations, PartOwnership.ToolOwned, clip.Index, clip.Name, clip.Reference)));
        parts.AddRange(extraction.Materials.Select(material => new ExtractedPart(
            ExtractKind.Materials, PartOwnership.TwoSided, material.Index, material.Name,
            material.Entry.Reference, material.Entry.GlbFingerprint, material.Entry.DocumentFingerprint)));
        parts.AddRange(extraction.Images.Select(image => new ExtractedPart(
            ExtractKind.Textures, PartOwnership.Blob, image.Index, image.Name,
            image.Entry.Reference, image.Entry.GlbFingerprint, image.Entry.DocumentFingerprint)));

        return new Extraction(extraction.Directory, parts);
    }

    /// <summary>Records <paramref name="extraction"/>, keeping the references half of the domain.</summary>
    public static void WriteExtraction(SidecarMeta meta, GlbExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(extraction);
        ExtractionRecord.Write(meta, GlbImporterName, ToRecord(extraction));
        WriteDomain(meta, Read(meta), ReadOptimization(meta));
    }

    /// <summary>The name the record is written under: the importer's, which is what tells one extractor's record from another's.</summary>
    internal const string GlbImporterName = "glb";

    /// <summary>
    /// The one writer of the domain, from parsed values, so the spelling is the same whichever
    /// half changed: the sidecar reader hands an inline table back as a plain one, and copying
    /// that through verbatim wrote it back as a <c>[glb.mesh]</c> section the next run undid.
    /// </summary>
    /// <remarks>
    /// What a GLB EXTRACTED to is not here any more — that is the engine's <see cref="ExtractionRecord"/>,
    /// the same record every extractor writes. What is left is the two things only a GLB has: the
    /// uris its container names, and the clip decimation its clips are cooked with.
    /// </remarks>
    private static void WriteDomain(SidecarMeta meta, IReadOnlyList<MeshReference> references, AnimationOptimizer.Setting? optimization)
    {
        var table = new CanonicalTomlTable();
        if (optimization is { } setting)
        {
            table.Add(OptimizeKey, new CanonicalInlineTable { { ToleranceKey, (double)setting.Tolerance }, { DistanceKey, (double)setting.Distance } });
        }

        if (references.Count > 0)
        {
            table.Add(ReferencesKey, references.Select(reference => (object)new CanonicalInlineTable
            {
                { SlotKey, reference.Slot },
                { UriKey, reference.Uri },
                { AssetReferenceCodec.GuidKey, DocumentGuid.Format(reference.Reference.Guid) },
                { AssetReferenceCodec.PathKey, reference.Reference.Path },
            }).ToList());
        }

        if (table.Count == 0) meta.RemoveSetting(Domain);
        else meta.SetSetting(Domain, table);
    }

    private static AssetReference? ReadReference(object? value)
    {
        if (value is not (CanonicalTomlTable or CanonicalInlineTable)) return null;
        if (Lookup(value, AssetReferenceCodec.GuidKey) is not string guidText || !DocumentGuid.TryParse(guidText, out var guid)) return null;
        if (Lookup(value, AssetReferenceCodec.PathKey) is not string { Length: > 0 } path) return null;
        return new AssetReference(guid, path);
    }



    // A table at a domain's root reads back as a CanonicalTomlTable, one inside an array as a
    // CanonicalInlineTable; the record is the same either way, so both are read here.

    private static object? Lookup(object? table, string key) => table switch
    {
        CanonicalTomlTable plain => plain.Value(key),
        CanonicalInlineTable inline => inline.Value(key),
        _ => null,
    };


    private static MeshReference? ReadEntry(object entry)
    {
        if (entry is not CanonicalInlineTable table) return null;
        if (table.Value(SlotKey) is not string { Length: > 0 } slot) return null;
        if (table.Value(UriKey) is not string { Length: > 0 } uri) return null;
        if (table.Value(AssetReferenceCodec.GuidKey) is not string guidText) return null;
        if (table.Value(AssetReferenceCodec.PathKey) is not string { Length: > 0 } path) return null;
        if (!DocumentGuid.TryParse(guidText, out var guid) || guid == Guid.Empty) return null;
        return new MeshReference(slot, uri, new AssetReference(guid, path));
    }
}

/// <summary>
/// What a GLB has been extracted to, as its sidecar records it. The mesh, skeleton, clips and
/// prefab are plain references: the first three are tool-owned documents the build cooks from
/// the GLB, the prefab is the author's from the moment it is written, and none of them has a
/// second side to keep in step. A material or image is an authored file whose GLB side can
/// change under it, so those entries carry the two fingerprints of the last sync.
/// </summary>
public sealed record GlbExtraction(
    string? Directory,
    AssetReference? Mesh,
    AssetReference? Skeleton,
    IReadOnlyList<GlbExtraction.NamedReference> Clips,
    IReadOnlyList<GlbExtraction.NamedEntry> Materials,
    IReadOnlyList<GlbExtraction.NamedEntry> Images)
{
    public static readonly Guid MaterialsComponentId = Guid.Parse("bdc4fc87-d7b4-41f1-bc90-fc827005adfc");

    public const string MaterialsComponentType = "Paradise.Export.Data.MaterialsComponentData";

    public static GlbExtraction None { get; } = new(null, null, null, [], [], []);

    /// <summary>Whether the GLB's geometry ships: the mesh document exists. The watcher mints it, so this is only ever false for a GLB nobody has drained yet.</summary>
    public bool Extracted => Mesh is not null;

    /// <summary>Whether <c>extract</c> has run: something only it writes — a material, an image — is recorded. The watcher's documents alone are not that.</summary>
    public bool Authored => Materials.Count > 0 || Images.Count > 0;

    /// <summary>Every recorded entry with the site name <c>verify</c> and <c>refs</c> use for it, so the GLB's extracted files are references it holds like any other.</summary>
    public IEnumerable<(string Where, AssetReference Reference)> Entries()
    {
        if (Mesh is { } mesh) yield return ("extract.mesh", mesh);
        if (Skeleton is { } skeleton) yield return ("extract.skeleton", skeleton);
        foreach (var clip in Clips) yield return ($"extract.clips[{clip.Index}]", clip.Reference);
        foreach (var material in Materials) yield return ($"extract.materials[{material.Index}]", material.Entry.Reference);
        foreach (var image in Images) yield return ($"extract.images[{image.Index}]", image.Entry.Reference);
    }

    /// <summary>The same record with every entry's path half brought up to date through <paramref name="resolve"/>; the input when none moved.</summary>
    public GlbExtraction Repointed(Func<AssetReference, AssetReference?> resolve, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(changes);

        AssetReference? Repoint(AssetReference? reference, string where)
        {
            if (reference is null || resolve(reference) is not { } current || current == reference) return reference;
            changes.Add($"{where}: {reference.Path} -> {current.Path}");
            return current;
        }

        Entry RepointEntry(Entry entry, string where) => entry with { Reference = Repoint(entry.Reference, where)! };

        return this with
        {
            Mesh = Repoint(Mesh, "extract.mesh"),
            Skeleton = Repoint(Skeleton, "extract.skeleton"),
            Clips = Clips.Select(c => c with { Reference = Repoint(c.Reference, $"extract.clips[{c.Index}]")! }).ToList(),
            Materials = Materials.Select(m => m with { Entry = RepointEntry(m.Entry, $"extract.materials[{m.Index}]") }).ToList(),
            Images = Images.Select(i => i with { Entry = RepointEntry(i.Entry, $"extract.images[{i.Index}]") }).ToList(),
        };
    }

    /// <param name="GlbFingerprint">SHA-256 of what the GLB extracted to at the last sync.</param>
    /// <param name="DocumentFingerprint">SHA-256 of the document's bytes at the last sync.</param>
    public sealed record Entry(AssetReference Reference, string GlbFingerprint, string DocumentFingerprint);

    /// <summary>
    /// An entry the GLB has several of, keyed by its glTF index: that is what the GLB's own draw
    /// slots bind by, and it survives a rename in the DCC, which a name does not; glTF names are
    /// optional and need not be unique. The name is the file's readable stem.
    /// </summary>
    public sealed record NamedEntry(int Index, string Name, Entry Entry);

    /// <summary>A clip's document, keyed like a <see cref="NamedEntry"/> but with no sync to record.</summary>
    public sealed record NamedReference(int Index, string Name, AssetReference Reference);
}

using Microsoft.Extensions.Logging;

using Paradise.Animation;
using Paradise.Animation.Offline;
using Paradise.Assets.Documents;
using Paradise.Assets.Gltf;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Paradise.Export.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>PNG/JPEG → KTX2 through the content-addressed cache.</summary>
public sealed class TextureImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".png", ".jpg", ".jpeg");

    /// <inheritdoc />
    public IReadOnlyList<IImportSettingsDomain> SettingsDomains => [TextureImportSettings.Instance];

    /// <inheritdoc />
    public string Name => "texture";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <summary>A texture ships as the KTX2 encoded from it, never as the PNG.</summary>
    public string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        return Path.ChangeExtension(asset.Path, ".ktx2");
    }

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(".png", ".jpg", ".jpeg")) return false;

        var settings = context.Meta!.Setting(TextureImportSettings.Domain);
        if (settings is not null && TextureImportSettings.Instance.Problem(settings) is { } problem)
        {
            errors.Add($"{context.Source}: sidecar {problem}");
            return true;
        }

        var preset = TextureImportSettings.Instance.PresetOf(settings) ?? DefaultPresetFor(context);
        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        UPath destination = "/" + Path.ChangeExtension(context.Source, ".ktx2");

        if (TextureStep.Encode(context, bytes, context.Asset.GetExtensionWithDot()!, preset, destination, errors))
        {
            ImporterLog.Encoded(context.Log, context.Source);
        }

        return true;
    }

    // Said out loud: a wrong guess is a colour-space bug with no other symptom, and the sidecar
    // is where to pin the answer.
    private static TexturePreset DefaultPresetFor(ImportContext context)
    {
        var preset = KtxTextureEncoder.FromKtxPreset(
            TextureEncodePolicy.PresetFromImageName(Path.GetFileNameWithoutExtension(context.Asset.GetName())));
        ImporterLog.PresetInferred(context.Log, context.Source, preset);
        return preset;
    }
}

/// <summary>A GLB is interchange and ships nothing: <c>extract</c> turns it into the blobs, materials and prefab the build reads instead. The importer claims it so it is never a stray, declares its image references so they follow moves, and refuses JSON glTF by name.</summary>
public sealed class GlbImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".glb", ".gltf");

    /// <inheritdoc />
    public IReadOnlyList<IImportSettingsDomain> SettingsDomains => [GlbImportSettings.Instance];

    /// <inheritdoc />
    public AssetReferences References(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!MeshContainer.IsMesh(asset) || context.Classify(asset) != AssetClass.Foreign) return AssetReferences.None;

        var relative = context.Relative(asset);
        var recorded = GlbImportSettings.BySlot(MeshReferences.Recorded(context.FileSystem, asset));
        var sites = new List<ReferenceSite>();
        foreach (var named in MeshContainer.Read(asset, context.FileSystem.ReadAllBytes(asset)))
        {
            var hint = MeshContainer.AssetPathFor(relative, named.Uri);
            if (recorded.TryGetValue(named.Slot, out var entry) && MeshContainer.SameUri(entry.Uri, named.Uri))
            {
                sites.Add(new ReferenceSite(named.Slot, entry.Reference, hint, named.Uri));
            }
            else
            {
                // A changed uri is a re-export: the recorded identity no longer describes what
                // the container spells, so the site is path-only until it is re-resolved.
                var note = recorded.ContainsKey(named.Slot) ? "changed its uri since its identity was recorded (a re-export)" : null;
                sites.Add(new ReferenceSite(named.Slot, null, hint, named.Uri, note));
            }
        }

        // What extract made of it is the GLB's too: a moved blob is followed, a removed one is
        // a dangling reference the author hears about, not a file that quietly re-mints.
        if (Extraction(context, asset) is { } extraction)
        {
            foreach (var (where, reference) in extraction.Entries())
            {
                sites.Add(new ReferenceSite(where, reference, reference.Path, reference.Path));
            }
        }

        return new AssetReferences(sites);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reconciliation = MeshReferences.Reconcile(context.FileSystem, context.Index, asset);
        var repaired = MeshReferences.Apply(context.FileSystem, asset, reconciliation, rewriteContainer: context.RewriteSources);

        if (Extraction(context, asset) is not { } extraction) return repaired;
        var changes = new List<string>();
        var repointed = extraction.Repointed(reference => Current(context.Index, reference), changes);
        if (changes.Count == 0) return repaired;

        var sidecar = SidecarMeta.PathFor(asset);
        var meta = SidecarMeta.Load(context.FileSystem, sidecar);
        GlbImportSettings.WriteExtraction(meta, repointed);
        meta.Save(context.FileSystem, sidecar);
        return new RepairedDocument(asset, [.. repaired?.Repointed ?? [], .. changes]);
    }

    /// <summary>What the sidecar records as extracted; null with no sidecar, or one verify already reports as unreadable.</summary>
    private static GlbExtraction? Extraction(ReferenceContext context, UPath asset)
    {
        var sidecar = SidecarMeta.PathFor(asset);
        if (!context.FileSystem.FileExists(sidecar)) return null;
        try
        {
            return GlbImportSettings.ReadExtraction(SidecarMeta.Load(context.FileSystem, sidecar));
        }
        catch (SidecarMetaException)
        {
            return null;
        }
    }

    /// <summary>Where the asset the reference names is now, or null to leave a reference that resolves to nothing as the evidence it is.</summary>
    private static AssetReference? Current(AssetIndex index, AssetReference reference)
    {
        var resolution = index.Resolve(reference);
        return resolution.Status is ReferenceStatus.Resolved or ReferenceStatus.Stale ? resolution.Current : null;
    }

    /// <inheritdoc />
    public string Name => "glb";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <summary>
    /// A GLB ships nothing; what a reference to it means at runtime is the mesh blob cooked from
    /// its <c>.mesh</c> document, and the GLB's sidecar records which document that is.
    /// </summary>
    public string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        var sidecar = SidecarMeta.PathFor(asset.Asset);
        GlbExtraction extraction;
        try
        {
            extraction = context.FileSystem.FileExists(sidecar)
                ? GlbImportSettings.ReadExtraction(SidecarMeta.Load(context.FileSystem, sidecar))
                : GlbExtraction.None;
        }
        catch (SidecarMetaException failure)
        {
            problem = $"references GLB '{asset.Path}', whose sidecar does not read: {failure.Message}";
            return null;
        }

        if (extraction.Mesh is not { } document)
        {
            problem = $"references GLB '{asset.Path}', which has no mesh document yet — a GLB ships nothing, its .mesh document is what the build cooks; run `paradise assets watch` (or `paradise assets extract {asset.Path}`) to mint it";
            return null;
        }

        var mesh = context.Resolve(document);
        if (!mesh.Found)
        {
            problem = $"references GLB '{asset.Path}', whose sidecar names mesh document '{document.Path}' (guid {DocumentGuid.Format(document.Guid)}), which no asset under assets/ carries";
            return null;
        }

        return mesh.Path;
    }

    /// <summary>
    /// Ships NOTHING: a GLB is interchange, and what the runtime draws is what <c>extract</c> made
    /// of it (<c>.mesh</c>, <c>.skeleton</c>, <c>.anim</c>, <c>.material</c>, the textures), each
    /// through its own importer. A GLB nobody extracted is <c>verify</c>'s warning, not a build
    /// error: the build is correct, there is just nothing of it to build.
    /// </summary>
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(".glb", ".gltf")) return false;

        // Claimed and refused, not declined: declining would let the mesh vanish silently.
        if (context.HasExtension(".gltf"))
        {
            errors.Add($"{context.Source}: is JSON glTF, which extract cannot read (it keeps textures and buffers as separate files); export it as .glb");
            return true;
        }

        return true;
    }
}

/// <summary>The encode-or-fetch every KTX2 output goes through, so a texture and a mesh's embedded image are cached and reported the same way.</summary>
internal static class TextureStep
{
    private const string CacheKind = "ktx2";

    public static string NoEncoder(ImportContext context) =>
        $"{context.Source}: no ktx CLI available to encode textures — run tools/ktx/KtxBootstrap, " +
        $"install KTX-Software, or set {KtxTool.PathEnvironmentVariable}";

    /// <summary>True when <paramref name="destination"/> now holds the KTX2; false with the error reported.</summary>
    public static bool Encode(ImportContext context, byte[] source, string sourceExtension, TexturePreset preset, UPath destination, List<string> errors)
    {
        if (context.Encoder is null)
        {
            errors.Add(NoEncoder(context));
            return false;
        }

        var quality = context.Profile.TextureQuality;
        var key = context.Encoder.CacheKey(source, sourceExtension, preset, quality);
        if (context.Cache.TryFetch(CacheKind, key, context.Output, destination)) return true;

        if (!context.Encoder.TryEncode(source, sourceExtension, preset, quality, out var ktx2, out var error))
        {
            errors.Add($"{context.Source}: texture encode failed: {error}");
            return false;
        }

        context.Output.WriteAllBytes(destination, ktx2);
        context.Cache.Store(CacheKind, key, context.Output, destination);
        return true;
    }
}

/// <summary>Committed audio banks: verified elsewhere, copied through byte-identical.</summary>
public sealed class AudioImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".bnk", ".wem");

    /// <inheritdoc />
    public string Name => "audio";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(".bnk", ".wem")) return false;

        context.Output.WriteAllBytes("/" + context.Source, context.FileSystem.ReadAllBytes(context.Asset));
        return true;
    }
}

/// <summary>Compiles one authoring document into the export contract; every document is baked, so a prop can be played on its own.</summary>
public sealed class PrefabImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(AssetClassifier.PrefabSuffix);

    /// <inheritdoc />
    public AssetReferences References(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Classify(asset) != AssetClass.Prefab) return AssetReferences.None;

        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(context.FileSystem, asset);
        }
        catch (PrefabDocumentException error)
        {
            return AssetReferences.Unreadable(error.Message);
        }

        var sites = DocumentReferences.Enumerate(document)
            .Select(found => new ReferenceSite(found.Where, found.Reference, found.Reference.Path, found.Reference.Path))
            .ToList();
        return new AssetReferences(sites);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A document's paths ARE its bytes; a build-time reconcile leaves them for --fix.
        return context.RewriteSources ? ReferenceRepair.FixDocument(context.FileSystem, context.Index, asset) : null;
    }

    /// <inheritdoc />
    public string Name => "prefab";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    public string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        return Path.ChangeExtension(asset.Path, DocumentOutput.PrefabExtension(context.Profile, context.Target));
    }

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(AssetClassifier.PrefabSuffix)) return false;
        if (DocumentOutput.Unsupported(context, errors)) return true;

        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(context.FileSystem, context.Asset);
        }
        catch (PrefabDocumentException error)
        {
            errors.Add(error.Message);
            return true;
        }

        var failures = new List<string>();
        var prefabExtension = DocumentOutput.PrefabExtension(context.Profile, context.Target);
        var configExtension = DocumentOutput.Extension(context.Profile);
        var level = PrefabBake.Bake(
            document, Referenced, prefabExtension, configExtension, failures,
            reference =>
            {
                var built = context.BuiltPath(reference, out var problem);
                if (problem is not null) failures.Add(problem);
                return built ?? reference.Path;
            });
        if (failures.Count > 0)
        {
            foreach (var failure in failures) errors.Add($"{context.Source}: {failure}");
            return true;
        }

        var text = DocumentOutput.PrefabAsJson(context.Profile, context.Target)
            ? ExportJsonWriter.SerializeToString(level)
            : ExportTomlWriter.SerializeToString(level);

        context.Output.WriteAllBytes(
            "/" + Path.ChangeExtension(context.Source, prefabExtension),
            DocumentOutput.Utf8NoBom.GetBytes(text));
        return true;

        PrefabDocument? Referenced(Paradise.Authoring.AssetReference reference)
        {
            try
            {
                // By guid: a prefab renamed outside `mv` still carries this instance's identity,
                // and the path half of the reference is only a hint. An unfound reference has no
                // file to open; verify already reported it, and the resolver reports the instance.
                var resolution = context.Resolve(reference);
                return resolution.Found ? PrefabDocumentSerializer.Load(context.FileSystem, resolution.Asset) : null;
            }
            catch (PrefabDocumentException)
            {
                return null;   // reported against the referenced document, which is also built
            }
        }
    }
}

/// <summary>
/// The cook behind <c>.mesh</c>, <c>.skeleton</c> and <c>.anim</c>: the document names a GLB and
/// a slot, the GLB is read through the context (so a re-export rebuilds every document that
/// names it, and a move of the GLB is a recorded input), and the slot's blob is written at the
/// document's own path. The GLB is cooked once per document; that is milliseconds, and the build
/// index skips the whole step when neither side changed.
/// </summary>
internal static class MeshReferenceStep
{
    public static bool Cook(ImportContext context, MeshSlot slot, List<string> errors)
    {
        MeshReferenceDocument document;
        try
        {
            document = MeshReferenceDocument.Parse(context.FileSystem.ReadAllText(context.Asset), context.Source);
        }
        catch (FormatException failure)
        {
            errors.Add(failure.Message);
            return true;
        }

        if (document.Slot != slot)
        {
            errors.Add($"{context.Source}: names slot '{MeshReferenceDocument.Spell(document.Slot)}' but its extension cooks a '{MeshReferenceDocument.Spell(slot)}'; the extension is what the build writes");
            return true;
        }

        var resolution = context.Resolve(document.Source);
        if (!resolution.Found)
        {
            errors.Add($"{context.Source}: names GLB '{document.Source.Path}' (guid {DocumentGuid.Format(document.Source.Guid)}), which no asset under assets/ carries");
            return true;
        }

        if (!MeshContainer.IsMesh(resolution.Asset))
        {
            errors.Add($"{context.Source}: names '{resolution.Path}' as its GLB, which is not one");
            return true;
        }

        CookedGlb cooked;
        try
        {
            cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(context.FileSystem.ReadAllBytes(resolution.Asset)));
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            errors.Add($"{context.Source}: {resolution.Path}: {error.Message}");
            return true;
        }

        byte[] blob;
        switch (slot)
        {
            case MeshSlot.Mesh:
                blob = Paradise.Assets.Mesh.MeshBlobFormat.Write(cooked.Mesh);
                break;

            case MeshSlot.Skeleton:
                if (cooked.Skeleton is null)
                {
                    errors.Add($"{context.Source}: {resolution.Path} has no node tree to cook a skeleton from");
                    return true;
                }

                blob = cooked.Skeleton;
                break;

            default:
                if (Clip(cooked, document, out var problem) is not { } clip)
                {
                    errors.Add($"{context.Source}: {resolution.Path} {problem}");
                    return true;
                }

                if (cooked.Skeleton is null)
                {
                    errors.Add($"{context.Source}: {resolution.Path} has no node tree to cook a clip over");
                    return true;
                }

                try
                {
                    blob = GltfCook.BuildClip(clip, cooked.Skeleton, Optimization(context, resolution.Asset));
                }
                catch (ArgumentException failure)
                {
                    errors.Add($"{context.Source}: {resolution.Path} {failure.Message}");
                    return true;
                }

                break;
        }

        context.Output.WriteAllBytes("/" + context.Source, blob);
        return true;
    }

    /// <summary>The GLB sidecar's <c>[glb] optimize</c>, read through the build's file system so a change to it rebuilds the clips; null keeps every key. A sidecar that will not parse is <c>verify</c>'s error to report; here it is a warning and a lossless clip, not a silent one.</summary>
    private static AnimationOptimizer.Setting? Optimization(ImportContext context, UPath glb)
    {
        var sidecar = SidecarMeta.PathFor(glb);
        if (!context.FileSystem.FileExists(sidecar)) return null;
        try
        {
            return GlbImportSettings.ReadOptimization(SidecarMeta.Load(context.FileSystem, sidecar));
        }
        catch (SidecarMetaException failure)
        {
            ImporterLog.SidecarUnreadableForClip(context.Log, context.Source, sidecar.ToString(), failure.Message);
            return null;
        }
    }

    /// <summary>The name decides when it names exactly one clip; the recorded hash finds a clip the DCC renamed; the index is the last tiebreak.</summary>
    internal static ClipData? Clip(CookedGlb cooked, MeshReferenceDocument document, out string problem)
    {
        problem = "";
        var named = document.Name is null ? [] : cooked.Clips.Where(clip => clip.Name == document.Name).ToList();
        if (named.Count == 1) return named[0];
        if (document.Hash is { } hash && cooked.Clips.FirstOrDefault(clip => GltfCook.ClipFingerprint(clip) == hash) is { } same) return same;
        if (document.Index is { } index && index < cooked.Clips.Count) return cooked.Clips[index];

        var available = cooked.Clips.Count == 0 ? "no clips" : "clips " + string.Join(", ", cooked.Clips.Select(clip => $"'{clip.Name}'"));
        problem = named.Count > 1
            ? $"has {named.Count} clips named '{document.Name}' and no index picks one; re-run `paradise assets extract` on the GLB"
            : $"has no clip named '{document.Name ?? $"#{document.Index}"}' ({available}); re-run `paradise assets extract` on the GLB, or delete this document";
        return null;
    }

    public static AssetReferences References(ReferenceContext context, UPath asset)
    {
        if (context.Classify(asset) != AssetClass.MeshReference) return AssetReferences.None;

        MeshReferenceDocument document;
        try
        {
            document = MeshReferenceDocument.Load(context.FileSystem, asset);
        }
        catch (FormatException failure)
        {
            return AssetReferences.Unreadable(failure.Message);
        }

        return new AssetReferences([new ReferenceSite("source", document.Source, document.Source.Path, document.Source.Path)]);
    }

    public static RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        if (!context.RewriteSources) return null;

        MeshReferenceDocument document;
        try
        {
            document = MeshReferenceDocument.Load(context.FileSystem, asset);
        }
        catch (FormatException)
        {
            return null;   // verify's finding
        }

        var resolution = context.Index.Resolve(document.Source);
        if (resolution.Status != ReferenceStatus.Stale) return null;

        context.FileSystem.WriteAllBytes(asset, (document with { Source = resolution.Current }).WriteBytes());
        return new RepairedDocument(asset, [$"{document.Source.Path} -> {resolution.Path}"]);
    }
}

/// <summary>The <c>*.mesh</c> step: a mesh reference cooked to the mesh blob (<see cref="Paradise.Assets.Mesh.MeshBlobFormat"/>) of the GLB it names.</summary>
public sealed class MeshImporter : IAssetImporter
{
    public string Name => "mesh";

    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(MeshReferenceDocument.MeshSuffix);

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
        => context.HasExtension(MeshReferenceDocument.MeshSuffix) && MeshReferenceStep.Cook(context, MeshSlot.Mesh, errors);

    /// <inheritdoc />
    public AssetReferences References(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MeshReferenceStep.References(context, asset);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MeshReferenceStep.Rewrite(context, asset);
    }
}

/// <summary>The <c>*.skeleton</c> and <c>*.anim</c> step: references cooked to ozz archives (<see cref="Paradise.Animation.OzzArchive"/>) of the GLB they name.</summary>
public sealed class AnimationImporter : IAssetImporter
{
    public string Name => "animation";

    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(MeshReferenceDocument.SkeletonSuffix, MeshReferenceDocument.ClipSuffix);

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (context.HasExtension(MeshReferenceDocument.SkeletonSuffix)) return MeshReferenceStep.Cook(context, MeshSlot.Skeleton, errors);
        if (context.HasExtension(MeshReferenceDocument.ClipSuffix)) return MeshReferenceStep.Cook(context, MeshSlot.Clip, errors);
        return false;
    }

    /// <inheritdoc />
    public AssetReferences References(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MeshReferenceStep.References(context, asset);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MeshReferenceStep.Rewrite(context, asset);
    }
}

/// <summary>
/// The <c>*.material</c> step: a config that references textures. Bakes each texture slot to the
/// KTX2 the texture step writes for it — by the reference's guid, through <see cref="ImportContext.Resolve"/>,
/// so a moved texture is a recorded input — and declares those references so the graph, <c>mv</c>,
/// <c>rm</c> and <c>verify</c> follow them.
/// </summary>
public sealed class MaterialImporter : IAssetImporter
{
    public string Name => "material";

    public bool RecordsIdentity => true;

    public string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        return Path.ChangeExtension(asset.Path, DocumentOutput.MaterialExtension(context.Profile));
    }

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(MaterialDocument.Suffix);

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(MaterialDocument.Suffix)) return false;
        if (DocumentOutput.Unsupported(context, errors)) return true;

        CanonicalTomlTable material;
        try
        {
            material = MaterialDocument.Parse(context.FileSystem.ReadAllText(context.Asset), context.Source);
        }
        catch (FormatException failure)
        {
            errors.Add(failure.Message);
            return true;
        }

        var before = errors.Count;
        var baked = MaterialDocument.Bake(material, reference =>
        {
            var built = context.BuiltPath(reference, out var problem);
            if (problem is not null) errors.Add($"{context.Source}: {problem}");
            return built;
        });
        if (errors.Count > before) return true;

        var extension = DocumentOutput.MaterialExtension(context.Profile);
        var text = context.Profile.DocumentFormat == DocumentFormat.Json
            ? CanonicalJson.ToNode(baked).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            : CanonicalTomlWriter.WriteString(baked);
        context.Output.WriteAllBytes(
            "/" + Path.ChangeExtension(context.Source, extension),
            DocumentOutput.Utf8NoBom.GetBytes(text));
        return true;
    }

    /// <inheritdoc />
    public AssetReferences References(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Classify(asset) != AssetClass.Material) return AssetReferences.None;

        CanonicalTomlTable material;
        try
        {
            material = MaterialDocument.Load(context.FileSystem, asset);
        }
        catch (FormatException failure)
        {
            return AssetReferences.Unreadable(failure.Message);
        }

        var sites = MaterialDocument.References(material)
            .Select(found => new ReferenceSite(found.Key, found.Reference, found.Reference.Path, found.Reference.Path))
            .ToList();
        return new AssetReferences(sites);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.RewriteSources) return null;

        CanonicalTomlTable material;
        try
        {
            material = MaterialDocument.Load(context.FileSystem, asset);
        }
        catch (FormatException)
        {
            return null;   // verify's finding
        }

        var repointed = new List<string>();
        var updated = MaterialDocument.Rewrite(material, reference =>
        {
            var resolution = context.Index.Resolve(reference);
            if (resolution.Status != ReferenceStatus.Stale) return reference;
            repointed.Add($"{reference.Path} -> {resolution.Path}");
            return resolution.Current;
        });
        if (updated is null) return null;

        context.FileSystem.WriteAllBytes(asset, CanonicalTomlWriter.WriteBytes(updated));
        return new RepairedDocument(asset, repointed);
    }
}

/// <summary>Authored config documents, compiled to the profile's document format.</summary>
public sealed class ConfigImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".toml") && !candidate.IsManifest;

    /// <inheritdoc />
    public string Name => "config";

    /// <summary>A config is addressed by path, not identity — its manifest entry carries no guid.</summary>
    public bool RecordsIdentity => false;

    public string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        return Path.ChangeExtension(asset.Path, DocumentOutput.Extension(context.Profile));
    }

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        // Compiling the manifest would ship the source project's profiles as game data.
        if (!context.HasExtension(".toml") || context.IsManifest) return false;
        if (DocumentOutput.Unsupported(context, errors)) return true;

        if (!ConfigDocument.TryCanonicalize(context.FileSystem.ReadAllText(context.Asset), out var canonical, out var error))
        {
            errors.Add($"{context.Source}: {error}");
            return true;
        }

        // Canonicalized even for JSON output, so a document refused as source is refused here too.
        var text = canonical;
        if (context.Profile.DocumentFormat == DocumentFormat.Json)
        {
            try
            {
                text = ConfigDocument.ToJson(canonical, context.Source);
            }
            catch (FormatException failure)
            {
                // inf/nan are legal TOML and have no JSON spelling; the error names source and key.
                errors.Add(failure.Message);
                return true;
            }
        }

        context.Output.WriteAllBytes(
            "/" + Path.ChangeExtension(context.Source, DocumentOutput.Extension(context.Profile)),
            DocumentOutput.Utf8NoBom.GetBytes(text));
        return true;
    }
}

internal static class DocumentOutput
{
    public static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    public static string Extension(BuildProfile profile)
        => profile.DocumentFormat == DocumentFormat.Json ? ".json" : ".toml";

    /// <summary>Play keeps <c>.prefab</c> (TOML inside) so spawners and the editor's Play button still name a file that exists; the runtime dispatches on extension.</summary>
    public static string PrefabExtension(BuildProfile profile, ProjectOutputTarget target)
        => target == ProjectOutputTarget.Play ? AssetClassifier.PrefabSuffix : Extension(profile);

    /// <summary>
    /// A material keeps its <c>.material</c> name in the build tree; what changes with the profile is
    /// the text inside (TOML or JSON), which a reader tells apart by its first character. Keeping the
    /// suffix is what lets a built prefab's slot list say what KIND of document it names, the same
    /// way <c>.mesh</c> and <c>.anim</c> do, instead of the format the build happened to choose.
    /// </summary>
    public static string MaterialExtension(BuildProfile profile) => MaterialDocument.Suffix;

    public static bool PrefabAsJson(BuildProfile profile, ProjectOutputTarget target)
        => target != ProjectOutputTarget.Play && profile.DocumentFormat == DocumentFormat.Json;

    /// <summary><see langword="true"/> when the importer must stop.</summary>
    public static bool Unsupported(ImportContext context, List<string> errors)
    {
        if (context.Profile.DocumentFormat is DocumentFormat.Toml or DocumentFormat.Json) return false;

        errors.Add($"{context.Source}: document_format \"{context.Profile.DocumentFormat}\" output is not implemented yet (toml and json are)");
        return true;
    }
}

/// <summary>The importers' log messages, in one place because they are spread over several importer classes.</summary>
/// <remarks><c>[LoggerMessage]</c> needs a partial class to generate into, and an importer is not
/// one. The <see cref="ILogger"/> parameters are NOT nullable, and cannot be: the generator emits
/// an unguarded <c>logger.IsEnabled(...)</c>, so a nullable parameter fails the build with CS8602
/// inside generated code. A build with nothing listening carries <c>NullLogger.Instance</c>
/// instead, which is why <see cref="ImportContext.Log"/> is non-nullable too.</remarks>
internal static partial class ImporterLog
{
    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "ktx2: {Source}")]
    public static partial void Encoded(ILogger logger, string source);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "texture: {Source} has no preset in its sidecar; {Preset} inferred from the name")]
    public static partial void PresetInferred(ILogger logger, string source, TexturePreset preset);

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning, Message = "clip: {Source} cooked with every key because its GLB's sidecar {Sidecar} does not parse ({Problem}); `paradise assets verify` names the fault")]
    public static partial void SidecarUnreadableForClip(ILogger logger, string source, string sidecar, string problem);

    [LoggerMessage(EventId = 32, Level = LogLevel.Information, Message = "{Source}: {Note}")]
    public static partial void PresetNote(ILogger logger, string source, string note);
}

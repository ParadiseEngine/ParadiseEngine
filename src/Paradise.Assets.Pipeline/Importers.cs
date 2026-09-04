using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
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

/// <summary>GLB copy-through with texture references repointed to KTX2; embedded PNG/JPEG is externalised to <c>&lt;stem&gt;_&lt;i&gt;.ktx2</c> beside the mesh through the same cache the texture step uses.</summary>
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

        return new AssetReferences(sites);
    }

    /// <inheritdoc />
    public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reconciliation = MeshReferences.Reconcile(context.FileSystem, context.Index, asset);
        return MeshReferences.Apply(context.FileSystem, asset, reconciliation, rewriteContainer: context.RewriteSources);
    }

    /// <inheritdoc />
    public string Name => "glb";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(".glb", ".gltf")) return false;

        // Claimed and refused, not declined: declining would let the mesh vanish silently.
        if (context.HasExtension(".gltf"))
        {
            errors.Add(
                $"{context.Source}: is JSON glTF, which this step cannot repoint (it keeps textures and " +
                "buffers as separate files); export it as .glb");
            return true;
        }

        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        var stem = Path.GetFileNameWithoutExtension(context.Asset.GetName());
        IReadOnlyList<EmbeddedImage> embedded = [];
        // A file the container reader cannot open copies through unchanged, as its references
        // do: what a mesh IS is the runtime's call, this step only follows the textures.
        if (GlbBinary.TryRead(bytes, out _, out _) && !GlbTextureRewriter.TryListEmbedded(bytes, stem, out embedded, out var problem))
        {
            errors.Add($"{context.Source}: {problem}");
            return true;
        }

        // By identity first: a texture renamed outside `mv` still carries the guid the sidecar
        // recorded for it, and the uri the DCC wrote is only a hint. Through Resolve, so the move
        // is a recorded input of this output.
        // Only where the container still spells the uri the entry was recorded from: a
        // re-export that changed a slot's uri has outrun its record, and following the old guid
        // there would bake the wrong texture (review of #244). That slot keeps the container's
        // own text, which the path check below validates or fails loudly.
        var recorded = GlbImportSettings.BySlot(MeshReferences.Recorded(context.FileSystem, context.Asset));
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var named in MeshContainer.Read(context.Asset, bytes))
        {
            if (!recorded.TryGetValue(named.Slot, out var entry) || !MeshContainer.SameUri(entry.Uri, named.Uri)) continue;
            var resolution = context.Resolve(entry.Reference);
            if (resolution.Found) uris[named.Slot] = MeshContainer.UriFor(context.Source, resolution.Path);
        }

        bytes = MeshContainer.RewriteUris(context.Asset, bytes, uris);

        var rewrite = MeshTextureReferences.Rewrite(bytes);

        // Against the SOURCE tree: Models/ builds before textures/, so the KTX2 does not exist yet.
        var missing = false;
        foreach (var reference in rewrite.Sources)
        {
            if (context.CheckReference(reference, out _) is not { } referenceProblem) continue;

            missing = true;
            errors.Add(referenceProblem);
        }

        if (missing) return true;

        UPath destination = "/" + context.Source;
        var glb = rewrite.Glb;
        if (embedded.Count > 0 && !TryExternalize(context, destination.GetDirectory(), glb, embedded, errors, out glb)) return true;

        context.Output.WriteAllBytes(destination, glb);
        return true;
    }

    /// <summary>Each embedded image becomes a sidecar under the mesh's own output directory, referenced like an authored texture: uri, mime and <c>KHR_texture_basisu</c> (issue #207: one contract for the built tree).</summary>
    private static bool TryExternalize(
        ImportContext context,
        UPath directory,
        byte[] glb,
        IReadOnlyList<EmbeddedImage> embedded,
        List<string> errors,
        out byte[] rewritten)
    {
        rewritten = glb;
        var before = errors.Count;
        if (context.Encoder is null && embedded.Any(image => !image.IsKtx2))
        {
            errors.Add(TextureStep.NoEncoder(context));
            return false;
        }

        foreach (var image in embedded)
        {
            var sidecar = directory / image.SidecarName;
            if (image.IsKtx2)
            {
                Ktx2Header.ForceLinearTransfer(image.Bytes);
                context.Output.WriteAllBytes(sidecar, image.Bytes);
                continue;
            }

            if (image.PresetNote is { } note) ImporterLog.PresetNote(context.Log, context.Source, note);
            TextureStep.Encode(context, image.Bytes, image.SourceExtension!, KtxTextureEncoder.FromKtxPreset(image.Preset), sidecar, errors);
        }

        if (errors.Count > before) return false;

        if (GlbTextureRewriter.TryExternalize(glb, embedded, out rewritten, out var problem)) return true;

        errors.Add($"{context.Source}: {problem}");
        return false;
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
            reference => context.Resolve(reference).Path);
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

/// <summary>Authored config documents, compiled to the profile's document format.</summary>
/// <summary>A blob the pipeline extracted (<c>.mesh</c>, <c>.skeleton</c>, <c>.anim</c>) ships as it is: the step checks the magic and copies the bytes, so an unrelated file with the extension is refused by name rather than shipped.</summary>
internal static class BlobStep
{
    public static bool Copy(ImportContext context, Func<ReadOnlySpan<byte>, bool> isBlob, string kind, List<string> errors)
    {
        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        if (!isBlob(bytes))
        {
            errors.Add($"{context.Source}: is not a Paradise {kind} blob (bad magic) — run `paradise assets extract` on its GLB to regenerate it");
            return true;
        }

        context.Output.WriteAllBytes("/" + context.Source, bytes);
        return true;
    }
}

/// <summary>The <c>*.mesh</c> step: a mesh blob (<see cref="Paradise.Assets.Mesh.MeshBlobFormat"/>) extracted from a GLB ships verbatim.</summary>
public sealed class MeshImporter : IAssetImporter
{
    public string Name => "mesh";

    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".mesh");

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
        => context.HasExtension(".mesh") && BlobStep.Copy(context, Paradise.Assets.Mesh.MeshBlobFormat.IsMeshBlob, "mesh", errors);
}

/// <summary>The <c>*.skeleton</c> and <c>*.anim</c> step: animation blobs (<see cref="Paradise.Animation.SkeletonFormat"/>, <see cref="Paradise.Animation.ClipFormat"/>) extracted from a GLB ship verbatim.</summary>
public sealed class AnimationImporter : IAssetImporter
{
    public string Name => "animation";

    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".skeleton", ".anim");

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (context.HasExtension(".skeleton")) return BlobStep.Copy(context, Paradise.Animation.SkeletonFormat.IsSkeleton, "skeleton", errors);
        if (context.HasExtension(".anim")) return BlobStep.Copy(context, Paradise.Animation.ClipFormat.IsClip, "clip", errors);
        return false;
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
            var resolution = context.Resolve(reference);
            if (!resolution.Found)
            {
                errors.Add($"{context.Source}: references texture '{reference.Path}' (guid {DocumentGuid.Format(reference.Guid)}), which no asset under assets/ carries");
                return null;
            }

            // The texture step writes its KTX2 at the source's own place in the tree.
            return Path.ChangeExtension(resolution.Path, ".ktx2");
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

public sealed class ConfigImporter : IAssetImporter
{
    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".toml") && !candidate.IsManifest;

    /// <inheritdoc />
    public string Name => "config";

    /// <summary>A config is addressed by path, not identity — its manifest entry carries no guid.</summary>
    public bool RecordsIdentity => false;

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

    /// <summary>A material builds to the same <c>.toml</c>/<c>.json</c> a config does: a host that reads <c>LevelMaterialData</c> by extension is unchanged.</summary>
    public static string MaterialExtension(BuildProfile profile) => Extension(profile);

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

    [LoggerMessage(EventId = 32, Level = LogLevel.Information, Message = "{Source}: {Note}")]
    public static partial void PresetNote(ILogger logger, string source, string note);
}

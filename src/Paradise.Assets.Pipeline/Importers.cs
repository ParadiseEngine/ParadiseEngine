using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Export.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// PNG/JPEG → KTX2 through the content-addressed cache; preset from the sidecar's
/// <c>[texture]</c> settings, filename tokens as the fallback.
/// </summary>
public sealed class TextureImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "texture";

    /// <summary>The encode depends on the argv and the encoder's version, which <see cref="ArtifactCache"/> keys on and the index does not.</summary>
    public bool DeterministicCopy => false;

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

        var preset = TextureImportSettings.Instance.PresetOf(settings) ?? DefaultPresetFor(context.Asset);
        var fast = context.Profile.TextureQuality == TextureQuality.Fast;
        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        UPath destination = "/" + Path.ChangeExtension(context.Source, ".ktx2");

        // The COMPLETE input of the step this key skips: source bytes, the exact argv shape
        // (which encodes preset and quality), and the tool itself.
        var argvToken = KtxCreate.BuildCreateArguments(KtxTextureEncoder.ToKtxPreset(preset), "out.ktx2", "in" + context.Asset.GetExtensionWithDot(), fast);
        var key = ArtifactDigest.Compute(bytes, argvToken, context.Encoder?.Identity ?? "");

        if (context.Encoder is not null && context.Cache.TryFetch("ktx2", key, context.Output, destination)) return true;

        if (context.Encoder is null)
        {
            errors.Add(
                $"{context.Source}: no ktx CLI available to encode textures — run tools/ktx/KtxBootstrap, " +
                $"install KTX-Software, or set {KtxCreate.KtxPathEnvironmentVariable}");
            return true;
        }

        if (!context.Encoder.TryEncode(bytes, context.Asset.GetExtensionWithDot()!, preset, fast, out var ktx2, out var error))
        {
            errors.Add($"{context.Source}: texture encode failed: {error}");
            return true;
        }

        context.Output.WriteAllBytes(destination, ktx2);
        context.Cache.Store("ktx2", key, context.Output, destination);
        context.Log?.Invoke($"ktx2: {context.Source} ({ktx2.Length} bytes)");
        return true;
    }

    /// <summary>
    /// The filename-token defaults the sidecar's <c>preset</c> overrides — the same heuristics
    /// <see cref="KtxCreate.PresetFromImageName"/> applies to GLB-internal images, applied to
    /// the file's stem.
    /// </summary>
    private static TexturePreset DefaultPresetFor(UPath path)
    {
        var image = new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(path.GetName()) };
        return KtxCreate.PresetFromImageName(image) switch
        {
            KtxCreate.TextureEncodingPreset.UastcNormalLinear => TexturePreset.Normal,
            KtxCreate.TextureEncodingPreset.UastcDataLinear => TexturePreset.Data,
            KtxCreate.TextureEncodingPreset.UastcColorLinear => TexturePreset.ColorLinear,
            _ => TexturePreset.Color,
        };
    }
}

/// <summary>
/// GLB copy-through with texture references repointed to KTX2 — <b>refusing</b> GLBs with
/// embedded PNG/JPEG until the externalization step lands, because a silently copied one would
/// fail in the runtime's KTX2-only reader with far less context.
/// </summary>
public sealed class MeshImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "mesh";

    /// <inheritdoc />
    public bool DeterministicCopy => true;

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.HasExtension(".glb", ".gltf")) return false;

        // CLAIMED and refused, rather than declined: both the rewrite and the missing-texture
        // check read the GLB container, so a JSON .gltf reaches neither — it would be copied
        // through with its .png URIs intact, shipping a mesh naming files the build never wrote,
        // which is the exact failure repointing exists to prevent. Declining would be worse
        // still: no importer below claims it either, so the mesh would vanish silently.
        if (context.HasExtension(".gltf"))
        {
            errors.Add(
                $"{context.Source}: is JSON glTF, which this step cannot repoint (it keeps textures and " +
                "buffers as separate files); export it as .glb");
            return true;
        }

        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        if (HasEmbeddedEncodedImages(bytes, out var mimeType))
        {
            errors.Add($"{context.Source}: has embedded {mimeType} textures; the mesh externalization step is not implemented yet");
            return true;
        }

        // The mesh names its textures as the author has them (../textures/rust.png); the build
        // writes them as KTX2 at the same relative place, so the copy has to be repointed.
        var rewrite = MeshTextureReferences.Rewrite(bytes);

        // Checked against the SOURCE tree, not the output: build order is alphabetical, so
        // Models/ is compiled before textures/ and the KTX2 does not exist yet. What matters is
        // that a source exists to compile at all — a reference naming nothing is a broken mesh
        // however the steps are ordered.
        var directory = context.Asset.GetDirectory();
        var missing = false;
        foreach (var reference in rewrite.Sources)
        {
            if (context.FileSystem.FileExists(Resolve(directory, reference))) continue;

            missing = true;
            errors.Add(
                $"{context.Source}: references texture '{reference}', which does not exist under assets/ " +
                "(a moved or renamed texture; the mesh and the reference move together)");
        }

        // A failure writes nothing (IAssetImporter's contract): the build already refuses to save
        // its index and manifest, and a rewritten GLB left behind anyway would be exactly the
        // half-built tree those refusals exist to prevent.
        if (missing) return true;

        context.Output.WriteAllBytes("/" + context.Source, rewrite.Glb);
        return true;
    }

    /// <summary>
    /// A glTF URI as a path in the assets tree. glTF URIs are '/'-separated and percent-encoded
    /// per the spec, and they are relative to the referencing document — never to the project
    /// root — which is what makes <c>../textures/x.png</c> mean what an author expects.
    /// </summary>
    private static UPath Resolve(UPath directory, string uri)
        => (directory / Uri.UnescapeDataString(uri)).ToAbsolute();

    /// <summary>
    /// Whether a GLB carries embedded (buffer-view-backed) PNG/JPEG images.
    /// </summary>
    internal static bool HasEmbeddedEncodedImages(byte[] glb, out string mimeType)
    {
        mimeType = "";
        if (!GlbBinary.TryRead(glb, out var gltf, out _)) return false;
        if (gltf["images"] is not JsonArray images) return false;
        foreach (var image in images)
        {
            var mime = image?["mimeType"]?.GetValue<string>();
            if (image?["bufferView"] is not null && mime is "image/png" or "image/jpeg")
            {
                mimeType = mime;
                return true;
            }
        }

        return false;
    }
}

/// <summary>Committed audio banks: verified elsewhere, copied through byte-identical.</summary>
public sealed class AudioImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "audio";

    /// <inheritdoc />
    public bool DeterministicCopy => true;

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

/// <summary>
/// Compiles one authoring document into the export contract the runtime loads.
/// </summary>
/// <remarks>
/// <b>Every document is baked, not just the ones a game calls levels.</b> There is one kind of
/// document, so a prop compiles to a one-entity level and can be played on its own — which is
/// the whole point of having one kind. A prefab referenced by another is ALSO flattened into
/// it, so the same objects appear in both outputs; that is what an instance means.
/// </remarks>
public sealed class PrefabImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "prefab";

    /// <summary>A prefab bakes the prefabs it instances, so its output changes when a file it merely REFERENCES does — nothing about its own bytes says so.</summary>
    public bool DeterministicCopy => false;

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
        var level = PrefabBake.Bake(document, Referenced, DocumentOutput.Extension(context.Profile), failures);
        if (failures.Count > 0)
        {
            foreach (var failure in failures) errors.Add($"{context.Source}: {failure}");
            return true;
        }

        // Both writers serialize the SAME baked LevelData through the same type model, so the
        // format is a choice about who reads the output rather than about what it says.
        var text = context.Profile.DocumentFormat == DocumentFormat.Json
            ? ExportJsonWriter.SerializeToString(level)
            : ExportTomlWriter.SerializeToString(level);

        context.Output.WriteAllBytes(
            "/" + Path.ChangeExtension(context.Source, DocumentOutput.Extension(context.Profile)),
            DocumentOutput.Utf8NoBom.GetBytes(text));
        return true;

        PrefabDocument? Referenced(Paradise.Authoring.AssetReference reference)
        {
            try
            {
                return PrefabDocumentSerializer.Load(context.FileSystem, context.AssetsRoot / reference.Path);
            }
            catch (PrefabDocumentException)
            {
                return null;   // reported against the referenced document, which is also built
            }
        }
    }
}

/// <summary>Authored config documents, compiled to the profile's document format.</summary>
public sealed class ConfigImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "config";

    /// <summary>The output depends on the profile's document format, which the index does not key on.</summary>
    public bool DeterministicCopy => false;

    /// <summary>A config is addressed by path, not identity — its manifest entry carries no guid.</summary>
    public bool RecordsIdentity => false;

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        // The manifest is the one `.toml` this importer must NOT claim: it configures the build
        // rather than being built by it, and compiling it into the output tree would ship the
        // source project's profiles as if they were game data.
        if (!context.HasExtension(".toml") || context.IsManifest) return false;
        if (DocumentOutput.Unsupported(context, errors)) return true;

        if (!ConfigDocument.TryCanonicalize(context.FileSystem.ReadAllText(context.Asset), out var canonical, out var error))
        {
            errors.Add($"{context.Source}: {error}");
            return true;
        }

        // Canonicalized first either way: the TOML reader is the one strict parser, so a document
        // that would be refused as source is refused whichever format it is compiled into.
        var text = context.Profile.DocumentFormat == DocumentFormat.Json
            ? ConfigDocument.ToJson(canonical, context.Source)
            : canonical;

        context.Output.WriteAllBytes(
            "/" + Path.ChangeExtension(context.Source, DocumentOutput.Extension(context.Profile)),
            DocumentOutput.Utf8NoBom.GetBytes(text));
        return true;
    }
}

/// <summary>
/// Copies sidecars verbatim — into the PLAY tree only. The editor playmode traces a built asset
/// back to its authoring identity, while a player's install has no use for source-tree
/// bookkeeping and must not ship it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target rule lives here, in the importer it is about.</b> This is the only step that
/// exists for one tree and not the other, and it says so itself by declining
/// (<see cref="ImportContext.Target"/>) — rather than the caller assembling a different chain
/// per flavour, which put the knowledge of what a sidecar is for in the one place that has no
/// business knowing it.
/// </para>
/// <para>
/// The one importer with a null <see cref="ImportContext.Meta"/>: a sidecar has no sidecar of
/// its own. Its manifest entry carries a null guid for the same reason the file exists -- a
/// sidecar DESCRIBES an identity rather than having one, and recording the guid it names would
/// give two manifest entries the same value and break any guid-to-asset lookup.
/// </para>
/// </remarks>
public sealed class SidecarImporter : IAssetImporter
{
    /// <inheritdoc />
    public string Name => "sidecar";

    /// <inheritdoc />
    public bool DeterministicCopy => true;

    /// <inheritdoc />
    public bool RecordsIdentity => false;

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (context.Target != ProjectOutputTarget.Play || !context.HasExtension(SidecarMeta.Suffix)) return false;

        context.Output.WriteAllBytes("/" + context.Source, context.FileSystem.ReadAllBytes(context.Asset));
        return true;
    }
}

/// <summary>What the document importers share: the profile's output format, spelled once.</summary>
internal static class DocumentOutput
{
    /// <summary>UTF-8 with no BOM, matching every other writer in the pipeline.</summary>
    public static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The extension an authored document gets in the build, per the profile.</summary>
    public static string Extension(BuildProfile profile)
        => profile.DocumentFormat == DocumentFormat.Json ? ".json" : ".toml";

    /// <summary>Reports an unimplemented document format; <see langword="true"/> when the importer must stop.</summary>
    public static bool Unsupported(ImportContext context, List<string> errors)
    {
        if (context.Profile.DocumentFormat is DocumentFormat.Toml or DocumentFormat.Json) return false;

        errors.Add($"{context.Source}: document_format \"{context.Profile.DocumentFormat}\" output is not implemented yet (toml and json are)");
        return true;
    }
}

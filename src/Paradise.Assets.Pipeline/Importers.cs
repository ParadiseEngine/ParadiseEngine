using System.Text.Json.Nodes;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Export.Serialization;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>PNG/JPEG → KTX2 through the content-addressed cache.</summary>
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

        var preset = TextureImportSettings.Instance.PresetOf(settings) ?? DefaultPresetFor(context);
        var fast = context.Profile.TextureQuality == TextureQuality.Fast;
        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        UPath destination = "/" + Path.ChangeExtension(context.Source, ".ktx2");

        // The key must cover the step's COMPLETE input, or it serves a stale encode as a hit.
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

    // Said out loud: a wrong guess is a colour-space bug with no other symptom, and the sidecar
    // is where to pin the answer.
    private static TexturePreset DefaultPresetFor(ImportContext context)
    {
        var preset = KtxCreate.PresetFromImageName(Path.GetFileNameWithoutExtension(context.Asset.GetName())) switch
        {
            KtxCreate.TextureEncodingPreset.UastcNormalLinear => TexturePreset.Normal,
            KtxCreate.TextureEncodingPreset.UastcDataLinear => TexturePreset.Data,
            KtxCreate.TextureEncodingPreset.UastcColorLinear => TexturePreset.ColorLinear,
            _ => TexturePreset.Color,
        };
        context.Log?.Invoke($"texture: {context.Source} has no preset in its sidecar; {preset} inferred from the name");
        return preset;
    }
}

/// <summary>GLB copy-through with texture references repointed to KTX2; embedded PNG/JPEG is refused here rather than failing later in the KTX2-only reader with less context.</summary>
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

        // Claimed and refused, not declined: declining would let the mesh vanish silently.
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
            // KtxCreate.ExternalizeTextures exists but works on System.IO paths and rewrites the
            // GLB in place, so it cannot run against this Zio mount (issue #212).
            errors.Add($"{context.Source}: has embedded {mimeType} textures; the build cannot externalize them yet — export with textures as separate files");
            return true;
        }

        var rewrite = MeshTextureReferences.Rewrite(bytes);

        // Against the SOURCE tree: Models/ builds before textures/, so the KTX2 does not exist yet.
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

        if (missing) return true;

        context.Output.WriteAllBytes("/" + context.Source, rewrite.Glb);
        return true;
    }

    private static UPath Resolve(UPath directory, string uri)
        => (directory / Uri.UnescapeDataString(uri)).ToAbsolute();

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

/// <summary>Compiles one authoring document into the export contract; every document is baked, so a prop can be played on its own.</summary>
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
        var prefabExtension = DocumentOutput.PrefabExtension(context.Profile, context.Target);
        var configExtension = DocumentOutput.Extension(context.Profile);
        var level = PrefabBake.Bake(document, Referenced, prefabExtension, configExtension, failures);
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

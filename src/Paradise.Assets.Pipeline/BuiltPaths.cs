using Paradise.Assets.Documents;
using Paradise.Authoring;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// Where the build writes the asset a reference names, so a built document can name it and the
/// runtime can open it without knowing how the build renames things. The one table of that
/// knowledge: a texture becomes its KTX2, a GLB becomes the mesh blob cooked from its
/// <c>.mesh</c> document (the GLB itself ships nothing), a prefab, material or config takes the
/// profile's document extension, and everything else — mesh, skeleton and clip documents, audio,
/// binaries — is written at its own path.
/// </summary>
/// <remarks>
/// Reads through the import context so the answers are recorded dependencies: a GLB whose
/// extraction changes rebuilds the prefabs that name it, the same way a renamed texture does.
/// </remarks>
internal static class BuiltPaths
{
    /// <summary>The built path of <paramref name="reference"/>, or null with <paramref name="problem"/> set when nothing will be built for it.</summary>
    public static string? Of(ImportContext context, AssetReference reference, out string? problem)
    {
        problem = null;
        var resolution = context.Resolve(reference);
        if (!resolution.Found)
        {
            problem = $"references '{reference.Path}' (guid {DocumentGuid.Format(reference.Guid)}), which no asset under assets/ carries";
            return null;
        }

        var path = resolution.Path;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png" or ".jpg" or ".jpeg":
                return Path.ChangeExtension(path, ".ktx2");

            case ".glb" or ".gltf":
                return MeshOf(context, resolution, out problem);

            case AssetClassifier.PrefabSuffix:
                return Path.ChangeExtension(path, DocumentOutput.PrefabExtension(context.Profile, context.Target));

            case MaterialDocument.Suffix:
                return Path.ChangeExtension(path, DocumentOutput.MaterialExtension(context.Profile));

            case ".toml":
                return Path.ChangeExtension(path, DocumentOutput.Extension(context.Profile));

            default:
                return path;
        }
    }

    /// <summary>A GLB's built form is the blob its <c>.mesh</c> document cooks to; the GLB's sidecar records which document that is.</summary>
    private static string? MeshOf(ImportContext context, ReferenceResolution glb, out string? problem)
    {
        problem = null;
        var sidecar = SidecarMeta.PathFor(glb.Asset);
        GlbExtraction extraction;
        try
        {
            extraction = context.FileSystem.FileExists(sidecar)
                ? GlbImportSettings.ReadExtraction(SidecarMeta.Load(context.FileSystem, sidecar))
                : GlbExtraction.None;
        }
        catch (SidecarMetaException failure)
        {
            problem = $"references GLB '{glb.Path}', whose sidecar does not read: {failure.Message}";
            return null;
        }

        if (extraction.Mesh is not { } document)
        {
            problem = $"references GLB '{glb.Path}', which has no mesh document yet — a GLB ships nothing, its .mesh document is what the build cooks; run `paradise assets watch` (or `paradise assets extract {glb.Path}`) to mint it";
            return null;
        }

        var mesh = context.Resolve(document);
        if (!mesh.Found)
        {
            problem = $"references GLB '{glb.Path}', whose sidecar names mesh document '{document.Path}' (guid {DocumentGuid.Format(document.Guid)}), which no asset under assets/ carries";
            return null;
        }

        return mesh.Path;
    }
}

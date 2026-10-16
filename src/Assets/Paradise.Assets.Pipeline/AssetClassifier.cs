using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

public enum AssetClass
{
    Manifest,

    /// <summary>A level, a prop, or a piece of one: there is no second kind, the build resolves instances away.</summary>
    Prefab,

    /// <summary>Any other <c>*.toml</c>.</summary>
    Config,

    /// <summary>A <c>*.material</c> document: a config that references textures.</summary>
    Material,

    /// <summary>A <c>*.mesh</c>, <c>*.skinnedmesh</c>, <c>*.skeleton</c> or <c>*.anim</c> model-part document.</summary>
    MeshReference,

    Sidecar,

    /// <summary>Listed in the project's <c>[assets] ignore</c>: never built, never given a sidecar, never a verify finding.</summary>
    Ignored,

    /// <summary>A model source, texture, bank or other asset outside the document suffixes known to this classifier.</summary>
    Foreign,
}

/// <summary>Classifies paths under <c>assets/</c> by case-insensitive suffix and the project's ignore list.</summary>
/// <remarks>Importer selection is separate and uses the recorded sidecar name or the importer's claim.</remarks>
public static class AssetClassifier
{
    public const string PrefabSuffix = ".prefab";

    public static AssetClass Classify(UPath assetsRoot, UPath path, AssetIgnoreRules ignore)
    {
        ArgumentNullException.ThrowIfNull(ignore);

        var name = path.GetName();
        // Sidecar first, so a sidecar minted for an ignored file is still seen and reported.
        if (SidecarMeta.IsSidecarPath(path)) return AssetClass.Sidecar;
        if (ignore.Matches(assetsRoot, path)) return AssetClass.Ignored;
        if (path == assetsRoot / AssetProjectLayout.ManifestFileName) return AssetClass.Manifest;
        if (name.EndsWith(PrefabSuffix, StringComparison.OrdinalIgnoreCase)) return AssetClass.Prefab;
        if (name.EndsWith(MaterialDocument.Suffix, StringComparison.OrdinalIgnoreCase)) return AssetClass.Material;
        if (MeshReferenceDocument.IsMeshReferencePath(path)) return AssetClass.MeshReference;
        if (name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return AssetClass.Config;
        return AssetClass.Foreign;
    }

    /// <summary>Everything under <c>assets/</c> needs one except a sidecar itself (infinite regress) and what the project ignores; a longer list of exceptions would be a list to maintain and remember.</summary>
    public static bool NeedsSidecar(AssetClass assetClass) => assetClass is not (AssetClass.Sidecar or AssetClass.Ignored);
}

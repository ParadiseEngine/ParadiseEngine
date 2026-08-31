using Paradise.Assets.Documents;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one file under <c>assets/</c> is to the pipeline.</summary>
public enum AssetClass
{
    /// <summary>The project manifest, <c>project.toml</c>.</summary>
    Manifest,

    /// <summary>
    /// An authoring document, <c>*.prefab</c> — a level, a prop, or a piece of one.
    /// </summary>
    /// <remarks>
    /// There is no second kind. What a game calls a level is a document you open at the top;
    /// what it calls a prop is the same document instantiated by another. The build resolves
    /// instances away either way, so nothing downstream sees the difference.
    /// </remarks>
    Prefab,

    /// <summary>An authored config document — any other <c>*.toml</c>.</summary>
    Config,

    /// <summary>A sidecar meta file, <c>*.meta</c>.</summary>
    Sidecar,

    /// <summary>A foreign/binary asset that must have a sidecar.</summary>
    Foreign,

    /// <summary>Anything else — carried along but not the pipeline's to interpret.</summary>
    Other,
}

/// <summary>
/// Classifies paths under <c>assets/</c> by extension.
/// </summary>
/// <remarks>
/// A path is <see cref="AssetClass.Foreign"/> exactly when an importer claims its extension
/// (<see cref="AssetImporters.Find(Zio.UPath)"/>) — the extension list lives with the importers, so adding
/// an asset type never edits this file. Deliberately closed — an unknown binary lands in
/// <see cref="AssetClass.Other"/> and is a <c>verify</c> warning rather than silently acquiring
/// pipeline semantics.
/// </remarks>
public static class AssetClassifier
{
    /// <summary>The authoring-document extension. The only one there is.</summary>
    public const string PrefabSuffix = ".prefab";

    /// <summary>Classifies <paramref name="path"/>, an absolute path under the assets tree.</summary>
    /// <param name="assetsRoot">The assets tree root the manifest check is relative to.</param>
    /// <param name="path">The file to classify.</param>
    public static AssetClass Classify(UPath assetsRoot, UPath path)
    {
        var name = path.GetName();
        if (SidecarMeta.IsSidecarPath(path)) return AssetClass.Sidecar;
        if (path == assetsRoot / Paradise.Assets.Project.AssetProjectLayout.ManifestFileName) return AssetClass.Manifest;
        if (name.EndsWith(PrefabSuffix, StringComparison.Ordinal)) return AssetClass.Prefab;
        if (name.EndsWith(".toml", StringComparison.Ordinal)) return AssetClass.Config;
        if (AssetImporters.Find(path) is not null) return AssetClass.Foreign;
        return AssetClass.Other;
    }

    /// <summary>Whether an asset of this class must have a sidecar.</summary>
    /// <remarks>
    /// <para>
    /// <b>Everything under <c>assets/</c> is an asset.</b> There is exactly one exception, and it
    /// is forced rather than chosen: a sidecar describing a sidecar is an infinite regress. The
    /// manifest, a baked <c>.navmesh.bin</c>, a Wwise <c>.xml</c> — all of them get one, because
    /// "the pipeline does not process this" and "this is not an asset" are different statements
    /// and only the first is ever true here.
    /// </para>
    /// <para>
    /// One rule, so identity has one home. The alternative — a list of things that do and do not
    /// need an id — is a list somebody has to maintain and everybody has to remember.
    /// </para>
    /// </remarks>
    public static bool NeedsSidecar(AssetClass assetClass) => assetClass != AssetClass.Sidecar;
}

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

    /// <summary>
    /// Anything else: a mesh, a texture, an audio bank — or a stray note nothing will ever
    /// build.
    /// </summary>
    /// <remarks>
    /// <b>Those are one class, because the classifier cannot tell them apart and no longer
    /// pretends to.</b> Whether anything handles a file is decided by the import chain, on
    /// whatever grounds each importer likes — extension, target, profile, or the bytes
    /// themselves — and that answer exists only while a build is running. What the classifier
    /// still knows is what it can see for itself: this is not one of the pipeline's own
    /// document kinds.
    /// </remarks>
    Foreign,
}

/// <summary>
/// Classifies paths under <c>assets/</c> by extension.
/// </summary>
/// <remarks>
/// <b>Only what a path can be read to say.</b> The three classes below the manifest are the
/// pipeline's own document kinds, spelled by suffix; everything else is
/// <see cref="AssetClass.Foreign"/>. This file asks the importers nothing, because there is
/// nothing they could truthfully answer outside a build: an importer claims an asset in its own
/// <see cref="IAssetImporter.Import"/>, on whatever grounds it likes, and a declined asset may
/// mean "not mine" or "not for this tree". Neither the classifier nor <c>verify</c> can tell
/// those apart, so neither guesses.
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
        return AssetClass.Foreign;
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

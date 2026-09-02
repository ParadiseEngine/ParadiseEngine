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

    Sidecar,

    /// <summary>Listed in the project's <c>[assets] ignore</c>: never built, never given a sidecar, never a verify finding.</summary>
    Ignored,

    /// <summary>A mesh, a texture, a bank, or a stray note; the classifier cannot tell them apart because only an importer, during a build, can claim a file.</summary>
    Foreign,
}

/// <summary>Classifies paths under <c>assets/</c> by suffix and the project's ignore list; it asks importers nothing because a declined asset may mean "not mine" or "not for this tree". Suffixes compare ignoring case, as the importers' own checks do, so <c>Foo.PREFAB</c> is a prefab to verify and to the build alike (issue #208).</summary>
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
        if (name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return AssetClass.Config;
        return AssetClass.Foreign;
    }

    /// <summary>Everything under <c>assets/</c> needs one except a sidecar itself (infinite regress) and what the project ignores; a longer list of exceptions would be a list to maintain and remember.</summary>
    public static bool NeedsSidecar(AssetClass assetClass) => assetClass is not (AssetClass.Sidecar or AssetClass.Ignored);
}

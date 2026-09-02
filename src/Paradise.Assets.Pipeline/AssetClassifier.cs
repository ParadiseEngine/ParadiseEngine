using Paradise.Assets.Documents;

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

    /// <summary>A mesh, a texture, a bank, or a stray note; the classifier cannot tell them apart because only an importer, during a build, can claim a file.</summary>
    Foreign,
}

/// <summary>Classifies paths under <c>assets/</c> by suffix only; it asks importers nothing because a declined asset may mean "not mine" or "not for this tree".</summary>
public static class AssetClassifier
{
    public const string PrefabSuffix = ".prefab";

    public static AssetClass Classify(UPath assetsRoot, UPath path)
    {
        var name = path.GetName();
        if (SidecarMeta.IsSidecarPath(path)) return AssetClass.Sidecar;
        if (path == assetsRoot / Paradise.Assets.Project.AssetProjectLayout.ManifestFileName) return AssetClass.Manifest;
        if (name.EndsWith(PrefabSuffix, StringComparison.Ordinal)) return AssetClass.Prefab;
        if (name.EndsWith(".toml", StringComparison.Ordinal)) return AssetClass.Config;
        return AssetClass.Foreign;
    }

    /// <summary>Everything under <c>assets/</c> needs one except a sidecar itself (infinite regress); a list of exceptions would be a list to maintain and remember. Junk files included — issue #203.</summary>
    public static bool NeedsSidecar(AssetClass assetClass) => assetClass != AssetClass.Sidecar;
}

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

    /// <summary>An editor's or the OS's scratch beside the assets: never built, never given a sidecar, never a verify finding.</summary>
    Junk,

    /// <summary>A mesh, a texture, a bank, or a stray note; the classifier cannot tell them apart because only an importer, during a build, can claim a file.</summary>
    Foreign,
}

/// <summary>Classifies paths under <c>assets/</c> by suffix only; it asks importers nothing because a declined asset may mean "not mine" or "not for this tree".</summary>
public static class AssetClassifier
{
    public const string PrefabSuffix = ".prefab";

    private static readonly string[] s_junkNames = [".DS_Store", "Thumbs.db", "desktop.ini"];

    private static readonly string[] s_junkSuffixes = ["~", ".tmp"];

    public static AssetClass Classify(UPath assetsRoot, UPath path)
    {
        var name = path.GetName();
        if (SidecarMeta.IsSidecarPath(path)) return AssetClass.Sidecar;
        if (IsJunk(path)) return AssetClass.Junk;
        if (path == assetsRoot / Paradise.Assets.Project.AssetProjectLayout.ManifestFileName) return AssetClass.Manifest;
        if (name.EndsWith(PrefabSuffix, StringComparison.Ordinal)) return AssetClass.Prefab;
        if (name.EndsWith(".toml", StringComparison.Ordinal)) return AssetClass.Config;
        return AssetClass.Foreign;
    }

    /// <summary>
    /// The one list of files the pipeline pretends are not there, shared by verify, the build
    /// walk, the sidecar maintainer and the watcher; a junk file that one of them sees and another
    /// ignores gets a sidecar committed for a file that is gitignored (issue #203).
    /// </summary>
    public static bool IsJunk(UPath path)
    {
        var name = path.GetName();
        foreach (var junk in s_junkNames)
        {
            if (string.Equals(name, junk, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var suffix in s_junkSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Emacs lock files, and Blender's numbered save versions (.blend1, .blend2, …).
        if (name.StartsWith(".#", StringComparison.Ordinal)) return true;
        return IsBlenderSaveVersion(name);
    }

    /// <summary>Everything under <c>assets/</c> needs one except a sidecar itself (infinite regress) and junk; a longer list of exceptions would be a list to maintain and remember.</summary>
    public static bool NeedsSidecar(AssetClass assetClass) => assetClass is not (AssetClass.Sidecar or AssetClass.Junk);

    private static bool IsBlenderSaveVersion(string name)
    {
        const string marker = ".blend";
        var at = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        var digits = name.AsSpan(at + marker.Length);
        if (digits.IsEmpty) return false;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        return true;
    }
}

using Paradise.Assets.Documents;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one file under <c>assets/</c> is to the pipeline.</summary>
public enum AssetClass
{
    /// <summary>The project manifest, <c>project.toml</c>.</summary>
    Manifest,

    /// <summary>An authoring scene document, <c>*.scene</c>.</summary>
    Scene,

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
/// The foreign-asset extension list is the pipeline's policy, not the format's: it decides which
/// files <b>must</b> carry a sidecar and which build step touches them. Deliberately closed —
/// an unknown binary lands in <see cref="AssetClass.Other"/> and is a <c>verify</c> warning
/// rather than silently acquiring pipeline semantics.
/// </remarks>
public static class AssetClassifier
{
    /// <summary>The scene-document double extension.</summary>
    public const string SceneSuffix = ".scene";

    private static readonly Dictionary<string, SidecarAssetKind> s_foreignKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".glb"] = SidecarAssetKind.Mesh,
        [".gltf"] = SidecarAssetKind.Mesh,
        [".png"] = SidecarAssetKind.Texture,
        [".jpg"] = SidecarAssetKind.Texture,
        [".jpeg"] = SidecarAssetKind.Texture,
        [".bnk"] = SidecarAssetKind.Audio,
        [".wem"] = SidecarAssetKind.Audio,
    };

    /// <summary>Classifies <paramref name="path"/>, an absolute path under the assets tree.</summary>
    /// <param name="assetsRoot">The assets tree root the manifest check is relative to.</param>
    /// <param name="path">The file to classify.</param>
    public static AssetClass Classify(UPath assetsRoot, UPath path)
    {
        var name = path.GetName();
        if (SidecarMeta.IsSidecarPath(path)) return AssetClass.Sidecar;
        if (path == assetsRoot / Paradise.Assets.Project.AssetProjectLayout.ManifestFileName) return AssetClass.Manifest;
        if (name.EndsWith(SceneSuffix, StringComparison.Ordinal)) return AssetClass.Scene;
        if (name.EndsWith(".toml", StringComparison.Ordinal)) return AssetClass.Config;
        if (s_foreignKinds.ContainsKey(path.GetExtensionWithDot() ?? string.Empty)) return AssetClass.Foreign;
        return AssetClass.Other;
    }

    /// <summary>The sidecar kind a foreign asset's extension implies, when it implies one.</summary>
    public static bool TryGetForeignKind(UPath path, out SidecarAssetKind kind)
        => s_foreignKinds.TryGetValue(path.GetExtensionWithDot() ?? string.Empty, out kind);
}

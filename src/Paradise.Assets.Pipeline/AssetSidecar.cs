using Paradise.Assets.Documents;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// An asset's sidecar as the pipeline holds it: the format's <see cref="SidecarMeta"/> with the
/// importer it names RESOLVED against the chain, so every consumer has the object and nothing
/// in the pipeline handles the name as text. The format layer keeps the name — it cannot know
/// the pipeline's types — and this is the one place it becomes an importer.
/// </summary>
public sealed class AssetSidecar
{
    private AssetSidecar(UPath asset, UPath path, SidecarMeta meta, IAssetImporter? importer)
    {
        Asset = asset;
        Path = path;
        Meta = meta;
        Importer = importer;
    }

    public UPath Asset { get; }

    public UPath Path { get; }

    /// <summary>The format-level record: identity and settings domains.</summary>
    public SidecarMeta Meta { get; }

    public Guid Guid => Meta.Guid;

    /// <summary>The importer the sidecar names; null when it names none (<see cref="ImporterName"/> null) or names one the chain does not have (<see cref="ImporterUnknown"/>).</summary>
    public IAssetImporter? Importer { get; }

    public string? ImporterName => Meta.Importer;

    /// <summary>The sidecar names an importer the chain lacks — a game's own importer missing from the list passed to <c>BuildHost.Run</c>, or a typo. Never resolved by a claim instead.</summary>
    public bool ImporterUnknown => Meta.Importer is not null && Importer is null;

    public CanonicalTomlTable? Setting(string domain) => Meta.Setting(domain);

    /// <summary>Loads and resolves; throws <see cref="SidecarMetaException"/> as <see cref="SidecarMeta.Load"/> does.</summary>
    public static AssetSidecar Load(IFileSystem fileSystem, UPath asset, IReadOnlyList<IAssetImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(importers);

        var path = SidecarMeta.PathFor(asset);
        return Resolve(asset, path, SidecarMeta.Load(fileSystem, path), importers);
    }

    /// <summary>Null when the asset has no sidecar or an unreadable one — the cases verify reports against the sidecar itself.</summary>
    public static AssetSidecar? TryLoad(IFileSystem fileSystem, UPath asset, IReadOnlyList<IAssetImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(importers);

        var path = SidecarMeta.PathFor(asset);
        if (!fileSystem.FileExists(path)) return null;
        try
        {
            return Resolve(asset, path, SidecarMeta.Load(fileSystem, path), importers);
        }
        catch (SidecarMetaException)
        {
            return null;
        }
    }

    /// <summary>A record already read, resolved.</summary>
    public static AssetSidecar Resolve(UPath asset, UPath path, SidecarMeta meta, IReadOnlyList<IAssetImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(importers);

        var importer = meta.Importer is { } name ? ImporterChain.Named(importers, name) : null;
        return new AssetSidecar(asset, path, meta, importer);
    }
}

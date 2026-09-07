using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Everything one run of <c>extract</c> needs, so the seam survives a new option.</summary>
/// <remarks>
/// A record rather than a parameter list because this crosses a public extension point: an option
/// added here reaches every extractor without breaking the ones a game already wrote.
/// </remarks>
/// <param name="Maintainer">The one minting authority — the watcher's when one is alive, so a document re-minted inside the quarantine window gets its held identity back.</param>
public sealed record ExtractRequest(
    IFileSystem FileSystem,
    AssetProjectLayout Layout,
    UPath Source,
    IReadOnlyList<IAssetImporter> Importers,
    ConflictResolution Resolution = ConflictResolution.Refuse,
    ILogger? Logger = null,
    bool GeneratePrefab = true,
    SidecarMaintainer? Maintainer = null);

/// <summary>
/// One source container the pipeline turns into authored assets: a GLB today, a game's own format
/// alongside it. The chain is a plain list a game's host passes to <c>BuildHost.Run</c>, the same
/// way it passes importers (issue #208).
/// </summary>
/// <remarks>
/// Extractors claim inside <see cref="Claims"/> rather than declaring extensions, for the reason
/// <see cref="IAssetImporter.Claims"/> gives: a project appends one that shadows a built-in on
/// whatever grounds it likes. Every predicate here takes the filesystem rather than bytes so an
/// extractor decides how much of a container it must read to answer — a path for one format, a
/// header for another.
/// </remarks>
public interface IAssetExtractor
{
    string Name { get; }

    /// <summary>Whether this extractor reads the container at <paramref name="source"/>. The one claim point; nothing else searches.</summary>
    bool Claims(IFileSystem fileSystem, UPath source);

    /// <summary>Whether the container holds anything the pipeline writes documents for at all — an empty one is not an error, just nothing to do.</summary>
    bool HasParts(IFileSystem fileSystem, UPath source);

    /// <summary>Whether it holds anything only <c>extract</c> writes (materials, embedded images); the documents the watcher mints on its own are not that.</summary>
    bool HasAuthoredParts(IFileSystem fileSystem, UPath source);

    /// <summary>Whether <c>extract</c> has already run for it, as the sidecar records.</summary>
    bool IsExtracted(IFileSystem fileSystem, UPath source);

    /// <summary>The full verb: every part, including the ones an author owns from the moment they exist.</summary>
    ExtractResult Extract(ExtractRequest request);

    /// <summary>
    /// Only the parts that carry no author work, for the watcher: minting those on a save is the
    /// same class of action as minting a sidecar, while writing a material or a prefab under an
    /// author is not.
    /// </summary>
    ExtractResult MintReferences(ExtractRequest request);
}

/// <summary>The built-in extractors, and how a chain answers "what reads this container".</summary>
public static class AssetExtractors
{
    /// <summary>Lowest precedence first: the chain is walked backwards so an appended extractor shadows the built-in it replaces.</summary>
    public static IReadOnlyList<IAssetExtractor> All { get; } = [new GlbExtractor()];

    /// <summary>The extractor that claims <paramref name="source"/>, or null when nothing does.</summary>
    public static IAssetExtractor? For(IReadOnlyList<IAssetExtractor> extractors, IFileSystem fileSystem, UPath source)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(fileSystem);

        for (var i = extractors.Count - 1; i >= 0; i--)
        {
            if (extractors[i].Claims(fileSystem, source)) return extractors[i];
        }

        return null;
    }

    /// <summary>The names in a chain, for a message that has to say what this build can read.</summary>
    public static string Known(IReadOnlyList<IAssetExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        return extractors.Count == 0 ? "none" : string.Join(", ", extractors.Select(extractor => extractor.Name));
    }
}

/// <summary>glTF binary: the engine's own container, and the reference implementation of the seam.</summary>
public sealed class GlbExtractor : IAssetExtractor
{
    /// <inheritdoc />
    public string Name => "glb";

    /// <inheritdoc />
    public bool Claims(IFileSystem fileSystem, UPath source) => MeshContainer.IsMesh(source);

    /// <inheritdoc />
    public bool HasParts(IFileSystem fileSystem, UPath source)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return fileSystem.FileExists(source) && MeshContainer.HasGeometry(source, fileSystem.ReadAllBytes(source));
    }

    /// <inheritdoc />
    public bool HasAuthoredParts(IFileSystem fileSystem, UPath source)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return fileSystem.FileExists(source) && AssetExtractor.HasAuthoredParts(fileSystem.ReadAllBytes(source));
    }

    /// <inheritdoc />
    /// <remarks>A sidecar that will not parse is the sidecar's own finding, not this one's: false keeps the caller quiet about a file already reported.</remarks>
    public bool IsExtracted(IFileSystem fileSystem, UPath source)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var sidecar = SidecarMeta.PathFor(source);
        if (!fileSystem.FileExists(sidecar)) return false;
        try
        {
            return GlbImportSettings.ReadExtraction(SidecarMeta.Load(fileSystem, sidecar)).Authored;
        }
        catch (SidecarMetaException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public ExtractResult Extract(ExtractRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssetExtractor.Extract(
            request.FileSystem, request.Layout, request.Source, request.Importers,
            request.Resolution, request.Logger, request.GeneratePrefab, request.Maintainer);
    }

    /// <inheritdoc />
    public ExtractResult MintReferences(ExtractRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssetExtractor.MintReferences(
            request.FileSystem, request.Layout, request.Source, request.Importers, request.Logger, request.Maintainer);
    }
}

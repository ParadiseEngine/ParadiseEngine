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

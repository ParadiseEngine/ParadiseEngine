using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Options and dependencies for one extraction.</summary>
/// <remarks>A request record lets public extractor implementations accept new options without changing signatures.</remarks>
/// <param name="Maintainer">Reuse the active watcher's minting authority to recover identities held in quarantine.</param>
/// <param name="Index">A scan of the layout's assets made since the tree last changed, so a caller running many sources saves each its own scan; null scans.</param>
public sealed record ExtractRequest(
    IFileSystem FileSystem,
    AssetProjectLayout Layout,
    UPath Source,
    IReadOnlyList<IAssetImporter> Importers,
    ConflictResolution Resolution = ConflictResolution.Refuse,
    ILogger? Logger = null,
    bool GeneratePrefab = true,
    SidecarMaintainer? Maintainer = null,
    AssetIndex? Index = null);

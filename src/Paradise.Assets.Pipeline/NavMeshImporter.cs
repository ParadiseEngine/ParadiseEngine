using DotRecast.Detour.Io;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Validates baked Detour navigation meshes and copies their bytes to the same built path.</summary>
public sealed class NavMeshImporter : IAssetImporter
{
    private const string Suffix = ".navmesh";

    /// <inheritdoc />
    public string Name => "navmesh";

    /// <inheritdoc />
    public bool RecordsIdentity => true;

    /// <inheritdoc />
    public bool Claims(ImportCandidate candidate) =>
        candidate.Asset.GetName().EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Import(ImportContext context, List<string> errors)
    {
        if (!context.Asset.GetName().EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var bytes = context.FileSystem.ReadAllBytes(context.Asset);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            // Match NavMeshBinaryWriter's Recast4J MeshSet format (cCompatibility: false).
            var mesh = new DtMeshSetReader().Read(reader);
            var hasPolygons = false;
            for (var index = 0; index < mesh.GetMaxTiles(); index++)
            {
                if (mesh.GetTile(index)?.data?.header?.polyCount > 0)
                {
                    hasPolygons = true;
                    break;
                }
            }

            if (!hasPolygons)
            {
                errors.Add($"{context.Source}: invalid baked Detour navmesh: The mesh contains no navigation polygons.");
                return true;
            }
        }
        catch (Exception failure) when (failure is IOException or ArgumentException
            or IndexOutOfRangeException or OverflowException)
        {
            errors.Add($"{context.Source}: invalid baked Detour navmesh: {failure.Message}");
            return true;
        }

        context.Output.WriteAllBytes("/" + context.Source, bytes);
        return true;
    }
}

using Paradise.Assets.Packing;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

/// <summary>Publishes a release pack without changing the incremental loose tree.</summary>
internal static class BuildPack
{
    internal static void Publish(IFileSystem fileSystem, UPath output, BuildManifest manifest, string document)
    {
        if (manifest.Assets.Any(asset => IsMetadata(asset.Path)))
            throw new InvalidDataException("An importer listed reserved build metadata as a packed asset.");
        using var metadata = new MemoryFileSystem();
        metadata.WriteAllText("/manifest.json", document);
        using var sources = new MountFileSystem(owned: true);
        sources.Mount("/built", new SubFileSystem(fileSystem, output, owned: false));
        sources.Mount("/metadata", new SubFileSystem(metadata, UPath.Root, owned: false));
        var entries = manifest.Assets
            // Wwise's native bank API takes physical paths, so these remain beside the pack.
            .Where(asset => !asset.Path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)
                && !asset.Path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            .Select(asset => new AssetPackFile(asset.Path, (UPath)"/built" / asset.Path))
            .Append(new AssetPackFile(BuildManifest.FileName, "/metadata/manifest.json"));
        var destination = output / AssetPackWriter.FileName;
        BuildOutput.Publish(fileSystem, destination, stream => AssetPackWriter.Write(sources, stream, entries));
    }

    internal static bool IsMetadata(string path)
    {
        string root = path.Split('/')[0];
        return string.Equals(root, AssetPackWriter.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, BuildManifest.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, BuildIndex.FileName, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }
}

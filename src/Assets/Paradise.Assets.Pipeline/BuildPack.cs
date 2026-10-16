using System.Security.Cryptography;

using Paradise.Assets.Packing;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline;

internal sealed record BuildPackStamp(string Manifest, byte[] Hash);

internal sealed record BuildPackRoot(string Requested, string Source);

/// <summary>Publishes a release pack without changing the incremental loose tree.</summary>
internal static class BuildPack
{
    internal static IReadOnlyList<BuildPackRoot> ResolveRoots(BuildProfile profile, AssetIndex sources)
    {
        var roots = new List<BuildPackRoot>(profile.PackRoots.Count);
        foreach (var root in profile.PackRoots)
        {
            var identity = Guid.TryParseExact(root, "D", out var guid)
                ? guid : sources.IdentityOf(sources.Root / root);
            if (identity is null || sources.Resolve(new AssetReference(identity.Value, root)) is not { Found: true } resolved)
                throw new InvalidDataException($"pack_roots: '{root}' does not identify an asset under assets/ (paths are case-exact).");
            roots.Add(new BuildPackRoot(root, resolved.Path));
        }
        return roots;
    }

    internal static BuildManifest Select(BuildManifest manifest, BuildProfile profile, IReadOnlyList<BuildPackRoot> roots, BuildIndex index)
    {
        if (!profile.HasPackRoots) return manifest;

        var outputs = manifest.Assets.ToDictionary(asset => asset.Path, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, string Referrer)>();
        foreach (var root in roots)
        {
            if (!index.Entries.TryGetValue(root.Source, out var entry) || entry.Assets.Count == 0)
                throw new InvalidDataException($"pack_roots: '{root.Requested}' has no cooked runtime output; select a prefab or cooked asset, not a build-only source.");
            foreach (var asset in entry.Assets) pending.Push((asset.Path, $"pack root '{root.Requested}'"));
        }

        while (pending.TryPop(out var next))
        {
            if (!selected.Add(next.Path)) continue;
            if (!outputs.TryGetValue(next.Path, out var asset))
                throw new InvalidDataException($"{next.Referrer} requires runtime asset '{next.Path}', which no importer produced. Runtime paths are case-exact, including on case-insensitive filesystems.");
            foreach (var dependency in index.Entries[asset.Source].RuntimeDependencies)
                pending.Push((dependency, $"'{asset.Path}'"));
        }

        return new BuildManifest
        {
            Project = manifest.Project,
            Profile = manifest.Profile,
            Assets = manifest.Assets.Where(asset => selected.Contains(asset.Path)).ToList(),
        };
    }

    internal static BuildPackStamp Publish(IFileSystem fileSystem, UPath output, BuildManifest manifest, string document, BuildPackStamp? previous)
    {
        if (manifest.Assets.Any(asset => IsMetadata(asset.Path)))
            throw new InvalidDataException("An importer listed reserved build metadata as a packed asset.");
        var destination = output / AssetPackWriter.FileName;
        // Hash the compressed file, not just its stamp: external damage can preserve size and mtime.
        if (previous?.Manifest == document && fileSystem.FileExists(destination)
            && Hash(fileSystem, destination).AsSpan().SequenceEqual(previous.Hash)) return previous;

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
        BuildOutput.Publish(fileSystem, destination, stream => AssetPackWriter.Write(sources, stream, entries));
        return new BuildPackStamp(document, Hash(fileSystem, destination));
    }

    private static byte[] Hash(IFileSystem fileSystem, UPath path)
    {
        using var input = fileSystem.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return SHA256.HashData(input);
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

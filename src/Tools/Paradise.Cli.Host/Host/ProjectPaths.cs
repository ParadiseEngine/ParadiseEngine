using Zio;

namespace Paradise.Cli;

/// <summary>Resolves ancestor symlinks before passing project paths to MSBuild or native processes.</summary>
internal static class ProjectPaths
{
    public static string Internal(IFileSystem fileSystem, UPath path) =>
        fileSystem.ConvertPathToInternal(ResolveLinks(fileSystem, path));

    /// <summary>The same location spelled without links, so containment checks survive a symlinked working tree.</summary>
    public static UPath ResolveLinks(IFileSystem fileSystem, UPath path)
    {
        if (path == UPath.Root) return path;
        // Aliased projects and physical references otherwise share obj with different identities.
        path = ResolveLinks(fileSystem, path.GetDirectory()) / path.GetName();
        if ((fileSystem.FileExists(path) || fileSystem.DirectoryExists(path))
            && (fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            && fileSystem.TryResolveLinkTarget(path, out var target))
            return ResolveLinks(fileSystem, target);
        return path;
    }
}

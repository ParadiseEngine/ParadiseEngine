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

    /// <summary>
    /// Resolves links above <paramref name="root"/> but not below it. Containment checks compare
    /// textually against the root, and a link inside the project must keep acting like itself:
    /// <c>rm</c> removes the link, not its target.
    /// </summary>
    public static UPath ResolveAbove(IFileSystem fileSystem, UPath path, UPath root)
    {
        for (var ancestor = path.GetDirectory(); !ancestor.IsNull && ancestor != UPath.Root; ancestor = ancestor.GetDirectory())
        {
            if (ResolveLinks(fileSystem, ancestor) == root)
                return root / path.FullName[(ancestor.FullName.Length + 1)..];
        }
        return ResolveLinks(fileSystem, path);
    }
}

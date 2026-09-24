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
    /// Rebases <paramref name="path"/> onto the physical tree when a linked ancestor reaches
    /// <paramref name="root"/> or a directory inside it — a symlinked working tree above the
    /// project, or an outside alias into <c>assets/</c>. Everything below that ancestor keeps the
    /// caller's spelling, so a link inside the project keeps acting like itself: <c>rm</c>
    /// removes the link, not its target. A path that never reaches the project stays as spelled.
    /// </summary>
    public static UPath ResolveInto(IFileSystem fileSystem, UPath path, UPath root)
    {
        // The shallowest matching ancestor wins: the loop walks deep to shallow, so the last
        // assignment preserves the most spelling.
        var result = path;
        for (var ancestor = path.GetDirectory(); !ancestor.IsNull && ancestor != UPath.Root; ancestor = ancestor.GetDirectory())
        {
            var resolved = ResolveLinks(fileSystem, ancestor);
            if (resolved == root || resolved.IsInDirectory(root, recursive: true))
                result = resolved / path.FullName[(ancestor.FullName.Length + 1)..];
        }
        return result;
    }
}

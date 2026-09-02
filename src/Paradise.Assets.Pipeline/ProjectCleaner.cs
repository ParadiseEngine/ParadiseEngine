using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The <c>clean</c> verb. Wholesale deletion is safe because the derived trees are pure functions of <c>assets/</c>; this is what retires the addon's prune apparatus, which existed because export output was committed.</summary>
public static class ProjectCleaner
{
    public static IReadOnlyList<UPath> Clean(IFileSystem fileSystem, AssetProjectLayout layout, bool keepEditor = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var removed = new List<UPath>();
        Remove(fileSystem, layout.Build, removed);
        if (!keepEditor) Remove(fileSystem, layout.Editor, removed);
        return removed;
    }

    private static void Remove(IFileSystem fileSystem, UPath directory, List<UPath> removed)
    {
        if (!fileSystem.DirectoryExists(directory)) return;
        fileSystem.DeleteDirectory(directory, isRecursive: true);
        removed.Add(directory);
    }
}

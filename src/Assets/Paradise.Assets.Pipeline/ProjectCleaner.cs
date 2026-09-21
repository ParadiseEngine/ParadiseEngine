using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Deletes regenerable build and editor output trees for the <c>clean</c> verb.</summary>
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

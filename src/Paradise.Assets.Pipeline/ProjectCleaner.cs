using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>
/// The <c>clean</c> verb: deletes the derived trees.
/// </summary>
/// <remarks>
/// Wholesale deletion is the design's acceptance invariant made executable — <c>.editor/</c> and
/// <c>build/</c> are pure functions of <c>assets/</c> plus tool versions, so deleting them loses
/// nothing but time. This is what retires the old prune apparatus: prune existed because export
/// output was committed and precious; gitignored output is simply removed.
/// </remarks>
public static class ProjectCleaner
{
    /// <summary>Deletes <c>build/</c> and <c>.editor/</c>, returning what was actually removed.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="keepEditor">
    /// Keep <c>.editor/</c> (the artifact cache and materialized working files). The default is
    /// to delete it too — a clean is a clean — but the cache is what turns the next build from
    /// minutes into seconds, so the CLI exposes this as <c>--keep-editor</c>.
    /// </param>
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

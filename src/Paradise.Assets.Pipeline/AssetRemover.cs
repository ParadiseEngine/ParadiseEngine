using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one <c>rm</c> did, or refused: the files it removed, and every reference that pointed at them.</summary>
/// <param name="Dangling">References into the removed (or refused) assets, as the graph had them. After a forced delete these are what <c>verify</c> will report.</param>
public sealed record RemoveResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ReferenceEdge> Dangling,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The <c>rm</c> verb: deletes an asset (or a directory of them) with its sidecars, unless
/// something still references it. Forced, it deletes anyway and reports every reference left
/// dangling; it never edits a document to hide one, because a nulled slot is evidence destroyed
/// and <c>verify</c> naming the reference is what lets the author decide what it should become.
/// </summary>
public static partial class AssetRemover
{
    public static RemoveResult Remove(
        IFileSystem fileSystem, AssetProjectLayout layout, UPath target, bool force = false, bool dryRun = false, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        target.AssertAbsolute(nameof(target));

        if (Problem(fileSystem, layout, target) is { } problem) return new RemoveResult(false, [problem], [], [], []);

        var isDirectory = fileSystem.DirectoryExists(target);
        var ignore = IgnoreRules(fileSystem, layout);
        var index = AssetIndex.Scan(fileSystem, layout.Assets, ignore);
        var graph = ReferenceGraph.Build(fileSystem, layout, index, ignore);

        var doomed = index.Files
            .Where(file => !SidecarMeta.IsSidecarPath(file))
            .Where(file => isDirectory ? file.IsInDirectory(target, recursive: true) : file == target)
            .ToList();

        // A reference from one doomed file to another is not dangling: both go together.
        var identities = doomed.Select(index.IdentityOf).OfType<Guid>().ToHashSet();
        var dangling = doomed
            .Select(index.IdentityOf).OfType<Guid>()
            .SelectMany(graph.DependentsOf)
            .Where(edge => !identities.Contains(edge.Referrer))
            .ToList();

        var warnings = graph.Unreadable.Count > 0
            ? [$"{graph.Unreadable.Count} file(s) could not be checked for references: {string.Join(", ", graph.Unreadable.Select(index.Relative))}"]
            : new List<string>();

        if (dangling.Count > 0 && !force)
        {
            var errors = new List<string>
            {
                $"'{index.Relative(target)}' is still referenced by {dangling.Select(edge => edge.ReferrerPath).Distinct().Count()} file(s); " +
                "re-point or remove those references, or pass --force to delete anyway and leave them for verify",
            };
            return new RemoveResult(false, errors, [], dangling, warnings);
        }

        var log = logger ?? NullLogger.Instance;
        var removed = doomed.Select(index.Relative).ToList();
        if (dryRun) return new RemoveResult(true, [], removed, dangling, warnings);

        try
        {
            if (isDirectory)
            {
                fileSystem.DeleteDirectory(target, isRecursive: true);
            }
            else
            {
                fileSystem.DeleteFile(target);
                var sidecar = SidecarMeta.PathFor(target);
                if (fileSystem.FileExists(sidecar)) fileSystem.DeleteFile(sidecar);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new RemoveResult(false, [$"could not remove '{index.Relative(target)}': {error.Message}"], [], dangling, warnings);
        }

        foreach (var relative in removed) LogRemoved(log, relative);
        foreach (var edge in dangling) LogDangling(log, index.Relative(edge.ReferrerPath), edge.Where, edge.Path);
        return new RemoveResult(true, [], removed, dangling, warnings);
    }

    private static string? Problem(IFileSystem fileSystem, AssetProjectLayout layout, UPath target)
    {
        if (!target.IsInDirectory(layout.Assets, recursive: true)) return $"'{target}' is not under {layout.Assets}; rm removes assets only";
        if (target == layout.Assets) return "the assets directory itself cannot be removed";
        if (target == layout.Manifest) return "project.toml is the project's identity and stays where it is";
        if (SidecarMeta.IsSidecarPath(target)) return $"'{target.GetName()}' is a sidecar; remove the asset it describes and it goes with it";
        if (!fileSystem.FileExists(target) && !fileSystem.DirectoryExists(target)) return $"'{target}' does not exist";
        return null;
    }

    private static AssetIgnoreRules IgnoreRules(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        try
        {
            return ProjectManifest.Load(fileSystem, layout.Manifest).Ignore;
        }
        catch (ProjectManifestException)
        {
            return AssetIgnoreRules.None;
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "removed: {Relative}")]
    private static partial void LogRemoved(ILogger logger, string relative);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "dangling: {Referrer} in {Where} still names '{Path}'")]
    private static partial void LogDangling(ILogger logger, string referrer, string where, string path);
}

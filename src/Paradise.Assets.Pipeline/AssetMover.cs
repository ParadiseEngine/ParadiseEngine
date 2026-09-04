using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What one <c>mv</c> did: the files that moved, the documents rewritten to follow them, and what it could not follow.</summary>
public sealed record MoveResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Rewritten,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The <c>mv</c> verb: moves a file or a directory under <c>assets/</c> with its sidecars, then
/// rewrites every asset reference in every prefab document to the new path. Identity never
/// changes — the sidecar travels as-is — so a reference's guid still names the same asset and
/// only its path half is touched. A rename outside this verb is not fatal — the guid still
/// resolves it and <c>verify</c> warns until <c>verify --fix</c> catches the path up — but this
/// is the tidy path: the documents follow the file in the same change (issue #208).
/// </summary>
/// <remarks>
/// A mesh's texture uris follow too, through its importer, where the sidecar records their
/// identities; a uri with no identity recorded has nothing to follow it by, so one this move
/// broke is reported for `verify --fix` to record (or the mesh to be re-exported) before verify
/// says the same thing with less context.
/// </remarks>
public static partial class AssetMover
{
    public static MoveResult Move(
        IFileSystem fileSystem, AssetProjectLayout layout, UPath from, UPath to, ILogger? logger = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        from.AssertAbsolute(nameof(from));
        to.AssertAbsolute(nameof(to));

        if (Problem(fileSystem, layout, ref from, ref to) is { } problem)
        {
            return new MoveResult(false, [problem], [], [], []);
        }

        var isDirectory = fileSystem.DirectoryExists(from);
        var before = AssetIndex.Scan(fileSystem, layout.Assets);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in before.Files)
        {
            if (SidecarMeta.IsSidecarPath(file)) continue;
            if (isDirectory ? file.IsInDirectory(from, recursive: true) : file == from)
            {
                var destination = isDirectory ? to / file.FullName[(from.FullName.Length + 1)..] : to;
                mapping[before.Relative(file)] = before.Relative(destination);
            }
        }

        var moved = new List<string>();
        var errors = new List<string>();
        try
        {
            var parent = to.GetDirectory();
            if (!fileSystem.DirectoryExists(parent)) fileSystem.CreateDirectory(parent);

            if (isDirectory)
            {
                Rename(fileSystem, from, to, fileSystem.MoveDirectory);
                moved.AddRange(mapping.Values);
            }
            else
            {
                Rename(fileSystem, from, to, fileSystem.MoveFile);
                moved.AddRange(mapping.Values);
                var sidecar = SidecarMeta.PathFor(from);
                if (fileSystem.FileExists(sidecar)) Rename(fileSystem, sidecar, SidecarMeta.PathFor(to), fileSystem.MoveFile);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // What did move is reported as moved: the caller must know the tree changed.
            errors.Add($"could not move '{before.Relative(from)}' to '{before.Relative(to)}': {error.Message}");
            return new MoveResult(false, errors, moved, [], []);
        }

        var log = logger ?? NullLogger.Instance;
        foreach (var (source, destination) in mapping) LogMoved(log, source, destination);

        var after = AssetIndex.Scan(fileSystem, layout.Assets);
        var ignore = IgnoreRules(fileSystem, layout);
        var chain = importers ?? AssetImporters.All;
        var graph = ReferenceGraph.Build(fileSystem, layout, after, ignore, chain);
        var context = new ReferenceContext(fileSystem, layout, after, ignore);
        var rewritten = new List<string>();
        var warnings = new List<string>();

        // Only what points at something that moved, plus the moved assets themselves — a mesh's
        // uris are relative to it, so moving it stales every one of them at once — plus what the
        // graph could not read (a document with no sidecar yet still references things) and
        // whatever holds a path-only site, which only its importer can judge. Everything else is
        // left byte for byte alone.
        var affected = new List<UPath>();
        foreach (var destination in mapping.Values)
        {
            var path = after.Root / destination;
            affected.Add(path);
            if (after.IdentityOf(path) is { } guid) affected.AddRange(graph.DependentFilesOf(guid));
        }

        affected.AddRange(graph.Unreadable);
        // A path-only holder only when THIS move touched it — it moved, or its target did. The
        // rest are `verify --fix`'s to record; recording them here dirtied every unrecorded mesh
        // on an unrelated move (review of #244).
        foreach (var (asset, site) in graph.PathOnly)
        {
            var relative = after.Relative(asset);
            if (mapping.Values.Contains(relative, StringComparer.Ordinal) || (site.Hint is { } hint && mapping.ContainsKey(hint)))
            {
                affected.Add(asset);
            }
        }

        foreach (var path in affected.Distinct())
        {
            try
            {
                if (ReferenceChain.Rewrite(chain, context, path) is not null)
                {
                    rewritten.Add(after.Relative(path));
                    LogRewrote(log, after.Relative(path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The files have moved; a document this could not follow is an error the author
                // fixes by hand, not a reason to leave the rest unrewritten.
                errors.Add($"{after.Relative(path)}: could not be rewritten to follow the move ({error.Message}); its references still name the old path");
            }
        }

        // A path-only site this move broke — the target moved away, or the holder moved away from
        // it — has nothing to follow it by; one already broken belongs to verify.
        foreach (var (asset, site) in graph.PathOnly)
        {
            var relative = after.Relative(asset);
            var holderMoved = mapping.Values.Contains(relative, StringComparer.Ordinal);
            var stillThere = site.Hint is { } hint && after.Contains(after.Root / hint);
            if (stillThere) continue;
            if (!holderMoved && !(site.Hint is { } hinted && mapping.ContainsKey(hinted))) continue;

            warnings.Add(
                $"{relative}: references '{site.Spelled}' in {site.Where} with no identity recorded, so the move could not " +
                "follow it — run `paradise assets verify --fix` to record the references, then move again, or re-export it");
        }

        return new MoveResult(errors.Count == 0, errors, moved, rewritten, warnings);
    }

    /// <summary>A case-only rename goes through a temporary name: on a case-insensitive disk the destination "exists" and a direct move is refused, yet it is the rename the case-exact reference rule makes most likely.</summary>
    private static void Rename(IFileSystem fileSystem, UPath from, UPath to, Action<UPath, UPath> move)
    {
        if (from == to) return;
        if (!IsCaseOnlyRename(from, to))
        {
            move(from, to);
            return;
        }

        var staging = from.GetDirectory() / $"{from.GetName()}.{Guid.NewGuid():N}.moving";
        move(from, staging);
        move(staging, to);
    }

    private static bool IsCaseOnlyRename(UPath from, UPath to)
        => from != to && string.Equals(from.FullName, to.FullName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves <c>mv a dir/</c> to <c>dir/a</c> and refuses everything a plain rename would silently get wrong.</summary>
    private static string? Problem(IFileSystem fileSystem, AssetProjectLayout layout, ref UPath from, ref UPath to)
    {
        if (!from.IsInDirectory(layout.Assets, recursive: true)) return $"'{from}' is not under {layout.Assets}; mv moves assets only";
        if (from == layout.Assets) return "the assets directory itself cannot be moved";
        if (from == layout.Manifest) return "project.toml is the project's identity and stays where it is";
        if (SidecarMeta.IsSidecarPath(from)) return $"'{from.GetName()}' is a sidecar; move the asset it describes and the sidecar follows";

        var isDirectory = fileSystem.DirectoryExists(from);
        if (!isDirectory && !fileSystem.FileExists(from)) return $"'{from}' does not exist";

        // On a case-insensitive disk `Models` → `models` looks like a move into itself.
        var caseOnly = IsCaseOnlyRename(from, to);
        if (!caseOnly && fileSystem.DirectoryExists(to)) to = to / from.GetName();

        if (!to.IsInDirectory(layout.Assets, recursive: true)) return $"'{to}' is not under {layout.Assets}; a build cannot ship what it does not own";
        if (SidecarMeta.IsSidecarPath(to)) return $"'{to.GetName()}' would be taken for a sidecar; assets cannot end in {SidecarMeta.Suffix}";
        if (!caseOnly && (fileSystem.FileExists(to) || fileSystem.DirectoryExists(to))) return $"'{to}' already exists; mv never overwrites";
        if (isDirectory && !caseOnly && to.IsInDirectory(from, recursive: true)) return $"'{to}' is inside '{from}'; a directory cannot move into itself";
        if (!isDirectory && to.GetExtensionWithDot() is null or "") return $"'{to.GetName()}' has no extension; nothing in the build could handle it";
        if (!isDirectory && !caseOnly && fileSystem.FileExists(SidecarMeta.PathFor(to)))
        {
            return $"'{SidecarMeta.PathFor(to).GetName()}' already exists beside the destination; it would then describe two assets";
        }

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

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "moved: {Source} -> {Destination}")]
    private static partial void LogMoved(ILogger logger, string source, string destination);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "rewrote: {Relative}")]
    private static partial void LogRewrote(ILogger logger, string relative);
}

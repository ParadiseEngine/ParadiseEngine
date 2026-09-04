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
/// A GLB's texture uris are inside the mesh and belong to the DCC that exported it; they are
/// not rewritten, and any that no longer resolve after the move are reported so the mesh can
/// be re-exported before verify says the same thing with less context.
/// </remarks>
public static partial class AssetMover
{
    public static MoveResult Move(IFileSystem fileSystem, AssetProjectLayout layout, UPath from, UPath to, ILogger? logger = null)
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
        var rewritten = new List<string>();
        var warnings = new List<string>();

        foreach (var path in after.Files)
        {
            var assetClass = AssetClassifier.Classify(layout.Assets, path, ignore);
            try
            {
                if (assetClass == AssetClass.Prefab)
                {
                    RewriteDocument(fileSystem, after, path, mapping, rewritten, warnings, log);
                }
                else if (assetClass == AssetClass.Foreign && path.GetExtensionWithDot() is { } extension
                    && string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
                {
                    WarnAboutMeshUris(fileSystem, after, path, mapping, warnings);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The files have moved; a document this could not follow is an error the author
                // fixes by hand, not a reason to leave the rest unrewritten.
                errors.Add($"{after.Relative(path)}: could not be rewritten to follow the move ({error.Message}); its references still name the old path");
            }
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

    private static void RewriteDocument(
        IFileSystem fileSystem,
        AssetIndex sources,
        UPath path,
        IReadOnlyDictionary<string, string> mapping,
        List<string> rewritten,
        List<string> warnings,
        ILogger log)
    {
        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(fileSystem, path);
        }
        catch (PrefabDocumentException error)
        {
            warnings.Add($"{sources.Relative(path)}: not rewritten — {error.Message}");
            return;
        }

        if (DocumentReferences.Rewrite(document, reference => Follow(reference, mapping)) is not { } updated) return;

        PrefabDocumentSerializer.Save(fileSystem, path, updated);
        rewritten.Add(sources.Relative(path));
        LogRewrote(log, sources.Relative(path));
    }

    /// <summary>Identity never changes in a move, so only the path half is followed.</summary>
    private static AssetReference Follow(AssetReference reference, IReadOnlyDictionary<string, string> mapping)
        => mapping.TryGetValue(reference.Path, out var moved) ? reference with { Path = moved } : reference;

    /// <summary>Only uris THIS move broke — the texture moved away, or the mesh moved away from it; a uri that was already broken belongs to verify.</summary>
    private static void WarnAboutMeshUris(IFileSystem fileSystem, AssetIndex sources, UPath glb, IReadOnlyDictionary<string, string> mapping, List<string> warnings)
    {
        var meshMoved = mapping.Values.Contains(sources.Relative(glb), StringComparer.Ordinal);
        var bytes = fileSystem.ReadAllBytes(glb);
        foreach (var uri in MeshTextureReferences.Rewrite(bytes).Sources)
        {
            var resolved = (glb.GetDirectory() / Uri.UnescapeDataString(uri)).ToAbsolute();
            if (sources.Contains(resolved)) continue;
            if (!meshMoved && !(sources.IsUnderRoot(resolved) && mapping.ContainsKey(sources.Relative(resolved)))) continue;

            warnings.Add(
                $"{sources.Relative(glb)}: references '{uri}' inside the GLB, which no longer resolves; " +
                "mv cannot rewrite a mesh — re-export it with the new path, or verify will refuse it");
        }
    }

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "moved: {Source} -> {Destination}")]
    private static partial void LogMoved(ILogger logger, string source, string destination);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "rewrote: {Relative}")]
    private static partial void LogRewrote(ILogger logger, string relative);
}

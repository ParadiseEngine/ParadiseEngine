using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>One document whose stale reference paths were caught up, and what moved.</summary>
/// <param name="Path">The document rewritten.</param>
/// <param name="Repointed">One line per reference, <c>old -> new</c>, for the console.</param>
public readonly record struct RepairedDocument(UPath Path, IReadOnlyList<string> Repointed);

/// <summary>
/// The <c>verify --fix</c> half: rewrites every reference whose path half no longer spells where
/// its guid lives.
/// </summary>
/// <remarks>
/// Tidiness, not correctness — the build resolves those references through <see cref="AssetIndex"/>
/// either way. It exists because a stale path is still noise in a diff and a lie in a grep, and
/// because <c>mv</c>'s eager rewrite cannot fire for a rename done in Finder. Output goes through
/// <c>PrefabDocumentSerializer</c>, so a fixed document is canonical and <c>prefab-check</c> stays
/// green.
/// </remarks>
public static class ReferenceRepair
{
    /// <summary>Fixes every asset under <c>assets/</c> its importer claims; an asset that will not parse is left for <c>verify</c> to report.</summary>
    public static IReadOnlyList<RepairedDocument> Fix(IFileSystem fileSystem, AssetProjectLayout layout, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        return Fix(fileSystem, layout, AssetIndex.Scan(fileSystem, layout.Assets, IgnoreRules(fileSystem, layout)), importers);
    }

    /// <summary>As <see cref="Fix(IFileSystem, AssetProjectLayout, IReadOnlyList{IAssetImporter})"/> over a scan already taken.</summary>
    public static IReadOnlyList<RepairedDocument> Fix(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, IReadOnlyList<IAssetImporter>? importers = null)
        => Run(fileSystem, layout, index, importers, rewriteSources: true);

    /// <summary>The sidecar half only, over every asset: what a reconcile at build time does, since a build must not move a path or a uri under an author's feet.</summary>
    public static IReadOnlyList<RepairedDocument> Reconcile(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, IReadOnlyList<IAssetImporter>? importers = null)
        => Run(fileSystem, layout, index, importers, rewriteSources: false);

    private static IReadOnlyList<RepairedDocument> Run(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, IReadOnlyList<IAssetImporter>? importers, bool rewriteSources)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);

        var repaired = new List<RepairedDocument>();
        if (!fileSystem.DirectoryExists(layout.Assets)) return repaired;

        var ignore = IgnoreRules(fileSystem, layout);
        var context = new ReferenceContext(fileSystem, layout, index, ignore, rewriteSources);
        var chain = importers ?? AssetImporters.All;

        // A sidecar without an importer is recorded first, through the maintainer (tooling owns
        // sidecars), so the references below are read by the importer the asset will keep.
        var maintainer = new SidecarMaintainer(fileSystem, layout, ignore: ignore, importers: chain);
        foreach (var path in index.Files)
        {
            if (SidecarMeta.IsSidecarPath(path)) continue;
            if (maintainer.RecordImporter(path) == SidecarAction.Refreshed) repaired.Add(new RepairedDocument(SidecarMeta.PathFor(path), ["importer recorded"]));
        }

        foreach (var path in index.Files)
        {
            if (SidecarMeta.IsSidecarPath(path) || index.IsIgnored(path)) continue;
            if (ReferenceChain.Rewrite(chain, context, path) is { } fixedAsset) repaired.Add(fixedAsset);
        }

        return repaired;
    }

    /// <summary>One document's stale paths caught up; null when nothing changed, or when it will not parse (verify's finding, not this pass's).</summary>
    public static RepairedDocument? FixDocument(IFileSystem fileSystem, AssetIndex index, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);

        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(fileSystem, path);
        }
        catch (PrefabDocumentException)
        {
            return null;
        }

        var repointed = new List<string>();
        var updated = DocumentReferences.Rewrite(document, reference =>
        {
            var resolution = index.Resolve(reference);
            if (resolution.Status != ReferenceStatus.Stale) return reference;

            repointed.Add($"{reference.Path} -> {resolution.Path}");
            return resolution.Current;
        });

        if (updated is null) return null;

        PrefabDocumentSerializer.Save(fileSystem, path, updated);
        return new RepairedDocument(path, repointed);
    }

    /// <summary>An unreadable manifest is verify's finding, not this pass's; nothing is ignored until it reads.</summary>
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
}

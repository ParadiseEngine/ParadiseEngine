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
    /// <summary>Fixes every document under <c>assets/</c>; a document that will not parse is left for <c>verify</c> to report.</summary>
    public static IReadOnlyList<RepairedDocument> Fix(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        return Fix(fileSystem, layout, AssetIndex.Scan(fileSystem, layout.Assets, IgnoreRules(fileSystem, layout)));
    }

    /// <summary>As <see cref="Fix(IFileSystem, AssetProjectLayout)"/> over a scan already taken.</summary>
    public static IReadOnlyList<RepairedDocument> Fix(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);

        var repaired = new List<RepairedDocument>();
        if (!fileSystem.DirectoryExists(layout.Assets)) return repaired;

        var ignore = IgnoreRules(fileSystem, layout);
        foreach (var path in index.Files)
        {
            var assetClass = AssetClassifier.Classify(layout.Assets, path, ignore);
            if (assetClass == AssetClass.Foreign && IsGlb(path))
            {
                if (FixMesh(fileSystem, index, path) is { } repairedMesh) repaired.Add(repairedMesh);
                continue;
            }

            if (assetClass != AssetClass.Prefab) continue;
            if (FixDocument(fileSystem, index, path) is { } repairedDocument) repaired.Add(repairedDocument);
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

    /// <summary>Whichever of <see cref="FixDocument"/> and <see cref="FixMesh"/> the file is.</summary>
    public static RepairedDocument? FixFile(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index, UPath path)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return AssetClassifier.Classify(layout.Assets, path, IgnoreRules(fileSystem, layout)) switch
        {
            AssetClass.Prefab => FixDocument(fileSystem, index, path),
            AssetClass.Foreign when IsGlb(path) => FixMesh(fileSystem, index, path),
            _ => null,
        };
    }

    /// <summary>
    /// A mesh's external references, reconciled into its sidecar (<see cref="MeshImportSettings"/>)
    /// and its uris caught up where the format can be written. Null when nothing changed.
    /// </summary>
    public static RepairedDocument? FixMesh(IFileSystem fileSystem, AssetIndex index, UPath mesh)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);

        return MeshReferences.Apply(fileSystem, mesh, MeshReferences.Reconcile(fileSystem, index, mesh), rewriteContainer: true);
    }

    /// <summary>The sidecar half of <see cref="FixMesh"/> over every mesh: what a reconcile does, since a build must not move uris under an author's feet.</summary>
    public static IReadOnlyList<RepairedDocument> StampMeshes(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);

        var ignore = IgnoreRules(fileSystem, layout);
        var stamped = new List<RepairedDocument>();
        foreach (var mesh in index.Files)
        {
            if (!IsGlb(mesh) || AssetClassifier.Classify(layout.Assets, mesh, ignore) != AssetClass.Foreign) continue;
            if (MeshReferences.Apply(fileSystem, mesh, MeshReferences.Reconcile(fileSystem, index, mesh), rewriteContainer: false) is { } repaired)
            {
                stamped.Add(repaired);
            }
        }

        return stamped;
    }

    internal static bool IsGlb(UPath path) => MeshContainer.IsMesh(path);

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

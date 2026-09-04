using Paradise.Assets.Documents;
using Paradise.Assets.Project;

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

        var sources = AssetPaths.Scan(fileSystem, layout.Assets);
        return Fix(fileSystem, layout, sources, AssetIndex.Build(fileSystem, sources, IgnoreRules(fileSystem, layout)));
    }

    /// <summary>As <see cref="Fix(IFileSystem, AssetProjectLayout)"/> over a scan and index already taken.</summary>
    public static IReadOnlyList<RepairedDocument> Fix(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetPaths sources, AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(index);

        var repaired = new List<RepairedDocument>();
        if (!fileSystem.DirectoryExists(layout.Assets)) return repaired;

        var ignore = IgnoreRules(fileSystem, layout);
        foreach (var path in sources.Files)
        {
            if (AssetClassifier.Classify(layout.Assets, path, ignore) != AssetClass.Prefab) continue;

            PrefabDocument document;
            try
            {
                document = PrefabDocumentSerializer.Load(fileSystem, path);
            }
            catch (PrefabDocumentException)
            {
                continue;   // reported by verify against the document itself
            }

            var repointed = new List<string>();
            var updated = DocumentReferences.Rewrite(document, reference =>
            {
                var resolution = index.Resolve(reference);
                if (resolution.Status != ReferenceStatus.Stale) return reference;

                repointed.Add($"{reference.Path} -> {resolution.Path}");
                return resolution.Current;
            });

            if (updated is null) continue;

            PrefabDocumentSerializer.Save(fileSystem, path, updated);
            repaired.Add(new RepairedDocument(path, repointed));
        }

        return repaired;
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

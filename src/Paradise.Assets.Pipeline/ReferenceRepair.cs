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
    /// A GLB's texture references: stamps <c>extras.paradise</c> onto every external image that
    /// lacks one and whose uri names an identified texture, then catches the uri (and the stamp's
    /// path) up to wherever each guid lives now. Null when the bytes did not change.
    /// </summary>
    /// <remarks>
    /// Stamping mutates a source file the DCC wrote. That is the contract (the reference lives in
    /// the file the DCC round-trips), and it is idempotent: a re-export that drops the block gets
    /// it back on the next pass, from the same uri, with the same guid.
    /// </remarks>
    public static RepairedDocument? FixMesh(IFileSystem fileSystem, AssetIndex index, UPath glb)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);

        var relative = index.Relative(glb);
        var bytes = fileSystem.ReadAllBytes(glb);
        var repointed = new List<string>();

        var stamped = GlbTextureReferences.Stamp(bytes, uri =>
        {
            if (GlbTextureReferences.AssetPathFor(relative, uri) is not { } assetPath) return null;
            if (index.IdentityOf(index.Root / assetPath) is not { } guid) return null;
            repointed.Add($"{uri} stamped as {assetPath}");
            return new AssetReference(guid, assetPath);
        });

        var followed = GlbTextureReferences.FollowUris(stamped, relative, reference =>
        {
            var resolution = index.Resolve(reference);
            if (resolution.Status != ReferenceStatus.Stale) return null;
            repointed.Add($"{reference.Path} -> {resolution.Path}");
            return resolution.Path;
        });

        if (ReferenceEquals(followed, bytes)) return null;

        fileSystem.WriteAllBytes(glb, followed);
        return new RepairedDocument(glb, repointed);
    }

    /// <summary>Only the stamping half of <see cref="FixMesh"/>, over every GLB: what a reconcile does, since a build must not silently move uris the author is looking at.</summary>
    public static IReadOnlyList<RepairedDocument> StampMeshes(IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(index);

        var ignore = IgnoreRules(fileSystem, layout);
        var stamped = new List<RepairedDocument>();
        foreach (var glb in index.Files)
        {
            if (!IsGlb(glb) || AssetClassifier.Classify(layout.Assets, glb, ignore) != AssetClass.Foreign) continue;

            var relative = index.Relative(glb);
            var bytes = fileSystem.ReadAllBytes(glb);
            var marks = new List<string>();
            var after = GlbTextureReferences.Stamp(bytes, uri =>
            {
                if (GlbTextureReferences.AssetPathFor(relative, uri) is not { } assetPath) return null;
                if (index.IdentityOf(index.Root / assetPath) is not { } guid) return null;
                marks.Add($"{uri} stamped as {assetPath}");
                return new AssetReference(guid, assetPath);
            });
            if (ReferenceEquals(after, bytes)) continue;

            fileSystem.WriteAllBytes(glb, after);
            stamped.Add(new RepairedDocument(glb, marks));
        }

        return stamped;
    }

    internal static bool IsGlb(UPath path)
        => string.Equals(path.GetExtensionWithDot(), ".glb", StringComparison.OrdinalIgnoreCase);

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

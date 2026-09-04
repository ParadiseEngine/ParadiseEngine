using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

public enum VerifySeverity
{
    /// <summary>Reported; does not fail the verb.</summary>
    Warning,

    Error,
}

/// <summary>One thing <c>verify</c> found, phrased for the person who must fix it.</summary>
public readonly record struct VerifyFinding(VerifySeverity Severity, UPath Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{(Severity == VerifySeverity.Error ? "error" : "warning")}: {Path}: {Message}";
}

/// <summary>The <c>verify</c> verb: the CI gate for the source tree. It never mutates the tree; minting sidecars is <c>watch</c>'s decision and catching a stale reference path up is <see cref="ReferenceRepair"/>'s, not a side effect of checking.</summary>
public static class ProjectVerifier
{
    /// <summary>Findings, errors first.</summary>
    public static IReadOnlyList<VerifyFinding> Verify(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var sources = AssetPaths.Scan(fileSystem, layout.Assets);

        // The ignore list twice: once here so the index matches the one a build takes, and once
        // inside so an unreadable manifest is reported rather than thrown.
        AssetIgnoreRules ignore;
        try
        {
            ignore = ProjectManifest.Load(fileSystem, layout.Manifest).Ignore;
        }
        catch (ProjectManifestException)
        {
            ignore = AssetIgnoreRules.None;
        }

        return Verify(fileSystem, layout, sources, AssetIndex.Build(fileSystem, sources, ignore));
    }

    /// <summary>As <see cref="Verify(IFileSystem, AssetProjectLayout)"/> over an existing scan and index, so a build verifies the same tree it then walks and resolves references the same way.</summary>
    public static IReadOnlyList<VerifyFinding> Verify(IFileSystem fileSystem, AssetProjectLayout layout, AssetPaths sources, AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(index);

        var findings = new List<VerifyFinding>();
        if (!fileSystem.DirectoryExists(layout.Assets))
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, layout.Assets, "the assets directory does not exist"));
            return findings;
        }

        var ignore = VerifyManifest(fileSystem, layout, findings)?.Ignore ?? AssetIgnoreRules.None;

        var guids = new Dictionary<Guid, UPath>();
        foreach (var path in sources.Files)
        {
            var assetClass = AssetClassifier.Classify(layout.Assets, path, ignore);
            if (assetClass == AssetClass.Ignored) continue;

            if (AssetClassifier.NeedsSidecar(assetClass)
                && !fileSystem.FileExists(SidecarMeta.PathFor(path)))
            {
                findings.Add(new VerifyFinding(
                    VerifySeverity.Error, path,
                    "has no sidecar — run `paradise assets watch` to mint one (tooling owns sidecars: a hand-typed guid cannot be checked against anything)"));
            }

            switch (assetClass)
            {
                case AssetClass.Sidecar:
                    VerifySidecar(fileSystem, layout.Assets, ignore, path, guids, findings);
                    break;

                case AssetClass.Prefab:
                    VerifyDocument(fileSystem, sources, index, path, findings);
                    break;

                // No "nothing handles this file" warning: only an importer, during a build, can
                // answer that, and a decline may mean "not for this tree" (issue #208).
            }
        }

        return findings
            .OrderBy(finding => finding.Severity == VerifySeverity.Error ? 0 : 1)
            .ThenBy(finding => finding.Path.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static ProjectManifest? VerifyManifest(IFileSystem fileSystem, AssetProjectLayout layout, List<VerifyFinding> findings)
    {
        try
        {
            return ProjectManifest.Load(fileSystem, layout.Manifest);
        }
        catch (ProjectManifestException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, layout.Manifest, error.Message));
            return null;
        }
    }

    private static void VerifySidecar(IFileSystem fileSystem, UPath assetsRoot, AssetIgnoreRules ignore, UPath path, Dictionary<Guid, UPath> guids, List<VerifyFinding> findings)
    {
        var asset = SidecarMeta.AssetPathFor(path);
        if (ignore.Matches(assetsRoot, asset))
        {
            // Minted before the file was ignored, and committed while the file it describes is
            // gitignored: every other checkout sees an orphan (#203). `watch` removes it.
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, path,
                $"describes '{asset.GetName()}', which [assets] ignore in project.toml excludes; delete the sidecar or run `paradise assets watch`"));
            return;
        }

        if (!fileSystem.FileExists(asset))
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, path,
                $"is orphaned — no '{asset.GetName()}' beside it (a move that skipped the tooling, or a deleted asset)"));
        }

        SidecarMeta meta;
        try
        {
            meta = SidecarMeta.Load(fileSystem, path);
        }
        catch (SidecarMetaException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
            return;
        }

        if (guids.TryGetValue(meta.Guid, out var first))
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, path,
                $"duplicates GUID '{DocumentGuid.Format(meta.Guid)}' of '{first}' — identities must be unique (a copied sidecar; re-mint one of them)"));
        }
        else
        {
            guids.Add(meta.Guid, path);
        }

        // Unknown domain: warning (a typo, or a newer pipeline's sidecar). Malformed known domain:
        // error, because the build would refuse it with less context.
        foreach (var (name, settings) in meta.Settings)
        {
            if (ImportSettings.Find(name) is not { } domain)
            {
                findings.Add(new VerifyFinding(
                    VerifySeverity.Warning, path,
                    $"carries [{name}] settings no build step reads — a misspelled domain, or a sidecar from a newer pipeline"));
                continue;
            }

            if (domain.Problem(settings) is { } problem)
            {
                findings.Add(new VerifyFinding(VerifySeverity.Error, path, problem));
            }
        }
    }

    private static void VerifyDocument(
        IFileSystem fileSystem, AssetPaths sources, AssetIndex index, UPath path, List<VerifyFinding> findings)
    {
        PrefabDocument document;
        try
        {
            document = PrefabDocumentSerializer.Load(fileSystem, path);
        }
        catch (PrefabDocumentException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
            return;
        }

        // A non-canonical document is a hand edit; the next machine write will reformat it, and
        // that diff belongs to this commit, not that one.
        var canonical = PrefabDocumentSerializer.Write(document);
        if (fileSystem.ReadAllText(path) != canonical)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Warning, path,
                "is valid but not in canonical form; rewrite it (prefab-check --fix) so machine edits stay out of your diffs"));
        }

        VerifyReferences(sources, index, path, document, findings);
        VerifyInstances(fileSystem, index, path, document, findings);
    }

    private static void VerifyReferences(
        AssetPaths sources, AssetIndex index, UPath path, PrefabDocument document, List<VerifyFinding> findings)
    {
        foreach (var (reference, where) in DocumentReferences.Enumerate(document))
        {
            Check(reference, where);
        }

        foreach (var problem in MalformedReferences(path, document))
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, problem));
        }

        void Check(Paradise.Authoring.AssetReference reference, string where)
        {
            var resolution = index.Resolve(reference);
            var guid = DocumentGuid.Format(reference.Guid);

            switch (resolution.Status)
            {
                // The path half names an asset whose own identity could not be read; that sidecar
                // carries its own finding, and repeating it per reference buries it.
                case ReferenceStatus.Resolved or ReferenceStatus.Undetermined:
                    return;

                // Not an error: the guid resolved it, so the build is correct and only the text is
                // out of date — a rename in Finder, or a sidecar the maintainer relinked by hash.
                case ReferenceStatus.Stale:
                    findings.Add(new VerifyFinding(
                        VerifySeverity.Warning, path,
                        $"in {where}, the path half says '{reference.Path}' but guid '{guid}' names " +
                        $"'{resolution.Path}'; the guid resolves it, so this builds — run " +
                        "`paradise assets verify --fix` to catch the path up"));
                    return;

                default:
                    findings.Add(new VerifyFinding(VerifySeverity.Error, path, Unresolved(sources, resolution, guid, where)));
                    return;
            }
        }
    }

    /// <summary>The guid names no asset in the tree, so the reference resolves to nothing; what the PATH half names decides how to say so.</summary>
    private static string Unresolved(AssetPaths sources, ReferenceResolution resolution, string guid, string where)
    {
        var reference = resolution.Reference;

        if (resolution.HintIdentity is { } other)
        {
            return $"in {where}, references guid '{guid}', which no asset under assets/ carries; " +
                $"'{reference.Path}' exists but is '{DocumentGuid.Format(other)}'. Re-point the " +
                "reference, or restore the asset whose identity this is";
        }

        if (sources.Problem(resolution.Asset, reference.Path) is { } problem)
        {
            return $"in {where}, {problem}, and no asset under assets/ carries its guid '{guid}'";
        }

        return $"in {where}, references guid '{guid}', which no asset under assets/ carries " +
            $"('{reference.Path}' exists but has no readable identity)";
    }

    /// <summary>References the walk could not read: the shape is reserved, so a table wearing it and failing to BE one must name the field rather than being skipped.</summary>
    private static IEnumerable<string> MalformedReferences(UPath path, PrefabDocument document)
    {
        foreach (var candidate in document.Objects)
        {
            foreach (var component in candidate.Components)
            {
                var name = component.Type ?? DocumentGuid.Format(component.Id);
                foreach (var (key, value) in component.Data)
                {
                    foreach (var problem in Walk(value, $"{name}.{key}")) yield return problem;
                }
            }
        }

        IEnumerable<string> Walk(object? value, string where)
        {
            switch (value)
            {
                case CanonicalInlineTable table when table.Count > 0 && AssetReferenceCodec.IsWrittenInline(table.ToList()):
                    if (AssetReferenceCodec.TryRead(table, out _)) break;

                    var reported = Problem(table, where);
                    if (reported is not null) yield return reported;
                    break;

                case CanonicalTomlTable nested:
                    foreach (var (key, member) in nested)
                    {
                        foreach (var problem in Walk(member, $"{where}.{key}")) yield return problem;
                    }

                    break;

                case IReadOnlyList<object> list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        foreach (var problem in Walk(list[i], $"{where}[{i}]")) yield return problem;
                    }

                    break;
            }
        }

        string? Problem(CanonicalInlineTable table, string where)
        {
            try
            {
                AssetReferenceCodec.Read(table, $"in {where}", message => new PrefabDocumentException(path.FullName, message));
                return null;
            }
            catch (PrefabDocumentException error)
            {
                return error.Message;
            }
        }
    }

    private static void VerifyInstances(
        IFileSystem fileSystem, AssetIndex index, UPath path, PrefabDocument document, List<VerifyFinding> findings)
    {
        if (!document.Objects.Any(o => o.Prefab is not null || o.Target is not null)) return;

        var result = PrefabResolver.Resolve(document, reference =>
        {
            try
            {
                return PrefabDocumentSerializer.Load(fileSystem, index.AssetOf(reference));
            }
            catch (PrefabDocumentException)
            {
                return null;   // reported against the prefab itself
            }
        });

        foreach (var error in result.Errors)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
        }
    }
}

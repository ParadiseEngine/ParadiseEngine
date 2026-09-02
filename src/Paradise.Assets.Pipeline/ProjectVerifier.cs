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

/// <summary>The <c>verify</c> verb: the CI gate for the source tree. It never mutates the tree; minting sidecars is <c>watch</c>'s decision, not a side effect of checking.</summary>
public static class ProjectVerifier
{
    /// <summary>Findings, errors first.</summary>
    public static IReadOnlyList<VerifyFinding> Verify(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        return Verify(fileSystem, layout, AssetPaths.Scan(fileSystem, layout.Assets));
    }

    /// <summary>As <see cref="Verify(IFileSystem, AssetProjectLayout)"/> over an existing scan, so a build verifies the same tree it then walks.</summary>
    public static IReadOnlyList<VerifyFinding> Verify(IFileSystem fileSystem, AssetProjectLayout layout, AssetPaths sources)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sources);

        var findings = new List<VerifyFinding>();
        if (!fileSystem.DirectoryExists(layout.Assets))
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, layout.Assets, "the assets directory does not exist"));
            return findings;
        }

        VerifyManifest(fileSystem, layout, findings);

        var guids = new Dictionary<Guid, UPath>();
        foreach (var path in sources.Files)
        {
            var assetClass = AssetClassifier.Classify(layout.Assets, path);
            if (assetClass == AssetClass.Junk) continue;

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
                    VerifySidecar(fileSystem, layout.Assets, path, guids, findings);
                    break;

                case AssetClass.Prefab:
                    VerifyDocument(fileSystem, layout, sources, path, findings);
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

    private static void VerifyManifest(IFileSystem fileSystem, AssetProjectLayout layout, List<VerifyFinding> findings)
    {
        try
        {
            ProjectManifest.Load(fileSystem, layout.Manifest);
        }
        catch (ProjectManifestException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, layout.Manifest, error.Message));
        }
    }

    private static void VerifySidecar(IFileSystem fileSystem, UPath assetsRoot, UPath path, Dictionary<Guid, UPath> guids, List<VerifyFinding> findings)
    {
        var asset = SidecarMeta.AssetPathFor(path);
        if (AssetClassifier.IsJunk(asset))
        {
            // Minted before junk was ignored, and committed while the file it describes is
            // gitignored: every other checkout sees an orphan (#203).
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, path,
                $"describes '{asset.GetName()}', which the pipeline ignores as editor or OS scratch; delete the sidecar"));
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

    private static void VerifyDocument(IFileSystem fileSystem, AssetProjectLayout layout, AssetPaths sources, UPath path, List<VerifyFinding> findings)
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

        VerifyReferences(fileSystem, layout, sources, path, document, findings);
        VerifyInstances(fileSystem, layout, path, document, findings);
    }

    private static void VerifyReferences(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetPaths sources, UPath path, PrefabDocument document, List<VerifyFinding> findings)
    {
        foreach (var candidate in document.Objects)
        {
            if (candidate.Prefab is { } prefab) Check(prefab, "prefab");

            foreach (var component in candidate.Components)
            {
                var name = component.Type ?? DocumentGuid.Format(component.Id);
                foreach (var (key, value) in component.Data) Walk(value, $"{name}.{key}");
            }
        }

        // Must match CanonicalJson's reference rule (the bake's hook): a reference the bake flattens is one verify checked.
        void Walk(object? value, string where)
        {
            switch (value)
            {
                // Gated on the reference SHAPE, not the model type: inside an array every table is
                // inline (#187), so a payload record would otherwise be reported as a bad reference.
                case CanonicalInlineTable table when table.Count > 0 && AssetReferenceCodec.IsWrittenInline(table.ToList()):
                    try
                    {
                        if (AssetReferenceCodec.Read(table, $"in {where}",
                                problem => new PrefabDocumentException(path.FullName, problem)) is { } reference)
                        {
                            Check(reference, where);
                        }
                    }
                    catch (PrefabDocumentException error)
                    {
                        findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
                    }

                    break;

                case CanonicalTomlTable nested:
                    foreach (var (key, member) in nested) Walk(member, $"{where}.{key}");
                    break;

                case IReadOnlyList<object> list:
                    for (var i = 0; i < list.Count; i++) Walk(list[i], $"{where}[{i}]");
                    break;
            }
        }

        void Check(Paradise.Authoring.AssetReference reference, string where)
        {
            var target = (layout.Assets / reference.Path).ToAbsolute();
            if (sources.Problem(target, reference.Path) is { } problem)
            {
                findings.Add(new VerifyFinding(VerifySeverity.Error, path, $"in {where}, {problem}"));
                return;
            }

            if (IdentityOf(fileSystem, target) is { } identity && identity != reference.Guid)
            {
                findings.Add(new VerifyFinding(
                    VerifySeverity.Error, path,
                    $"references '{reference.Path}' in {where} with guid " +
                    $"'{DocumentGuid.Format(reference.Guid)}', but that asset's identity is " +
                    $"'{DocumentGuid.Format(identity)}' — a half-finished move, and the two halves " +
                    "must name the same asset"));
            }
        }
    }

    private static Guid? IdentityOf(IFileSystem fileSystem, UPath target)
    {
        var sidecar = SidecarMeta.PathFor(target);
        if (fileSystem.FileExists(sidecar))
        {
            try
            {
                return SidecarMeta.Load(fileSystem, sidecar).Guid;
            }
            catch (SidecarMetaException)
            {
                return null;   // reported against the sidecar itself, not against every reference
            }
        }

        return null;
    }

    private static void VerifyInstances(
        IFileSystem fileSystem, AssetProjectLayout layout, UPath path, PrefabDocument document, List<VerifyFinding> findings)
    {
        if (!document.Objects.Any(o => o.Prefab is not null || o.Target is not null)) return;

        var result = PrefabResolver.Resolve(document, reference =>
        {
            try
            {
                return PrefabDocumentSerializer.Load(fileSystem, layout.Assets / reference.Path);
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

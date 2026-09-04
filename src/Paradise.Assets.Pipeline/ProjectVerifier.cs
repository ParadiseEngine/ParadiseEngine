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

        // The ignore list is read twice: once here so the scan matches the one a build takes,
        // and once inside so an unreadable manifest is reported rather than thrown.
        AssetIgnoreRules ignore;
        try
        {
            ignore = ProjectManifest.Load(fileSystem, layout.Manifest).Ignore;
        }
        catch (ProjectManifestException)
        {
            ignore = AssetIgnoreRules.None;
        }

        return Verify(fileSystem, layout, AssetIndex.Scan(fileSystem, layout.Assets, ignore));
    }

    /// <summary>As <see cref="Verify(IFileSystem, AssetProjectLayout)"/> over an existing scan, so a build verifies the same tree it then walks and resolves references the same way.</summary>
    public static IReadOnlyList<VerifyFinding> Verify(
        IFileSystem fileSystem, AssetProjectLayout layout, AssetIndex sources, IReadOnlyList<IAssetImporter>? importers = null)
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

        var ignore = VerifyManifest(fileSystem, layout, findings)?.Ignore ?? AssetIgnoreRules.None;

        var context = new ReferenceContext(fileSystem, layout, sources, ignore);
        var chain = importers ?? AssetImporters.All;
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
                    VerifySidecar(fileSystem, layout.Assets, ignore, path, guids, findings, chain);
                    break;

                case AssetClass.Prefab:
                    VerifyDocument(fileSystem, sources, path, findings);
                    break;

                case AssetClass.Material:
                    VerifyMaterial(fileSystem, path, findings);
                    break;

                case AssetClass.Foreign when MeshContainer.IsMesh(path):
                    VerifyExtracted(fileSystem, path, findings);
                    break;

                case AssetClass.Foreign when path.GetName().EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase):
                    findings.Add(new VerifyFinding(VerifySeverity.Error, path, "is KTX2, which is build output; author the PNG or JPEG it was encoded from and let the build write the KTX2"));
                    break;

                // No "nothing handles this file" warning: only an importer, during a build, can
                // answer that, and a decline may mean "not for this tree" (issue #208).
            }

            // References through the chain, whatever the asset: the importer that claims it reads
            // them, and the findings follow the one rule (the guid decides, the path is a hint). A
            // parse problem is not repeated here — the document's own check already said it.
            if (assetClass != AssetClass.Sidecar
                && ReferenceChain.Claim(chain, context, path) is { References.Problem: null } claimed)
            {
                findings.AddRange(ReferenceChain.Verify(context, path, claimed.References));
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

    private static void VerifySidecar(
        IFileSystem fileSystem, UPath assetsRoot, AssetIgnoreRules ignore, UPath path, Dictionary<Guid, UPath> guids, List<VerifyFinding> findings,
        IReadOnlyList<IAssetImporter> importers)
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

        var sidecar = AssetSidecar.Resolve(asset, path, meta, importers);
        VerifyImporter(fileSystem, assetsRoot, sidecar, importers, findings);

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
        // error, because the build would refuse it with less context. The recorded importer's own
        // domains first; then the chain's, since a domain another importer reads is not a typo.
        foreach (var (name, settings) in meta.Settings)
        {
            var domain = sidecar.Importer?.SettingsDomains.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                ?? ImportSettings.Find(name, importers);
            if (domain is null)
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

    /// <summary>The sidecar names its importer; a name the chain lacks is an error (a game's importer missing from the list, or a typo), and no name is a warning the tooling clears.</summary>
    private static void VerifyImporter(
        IFileSystem fileSystem, UPath assetsRoot, AssetSidecar sidecar, IReadOnlyList<IAssetImporter> importers, List<VerifyFinding> findings)
    {
        if (!fileSystem.FileExists(sidecar.Asset)) return;   // the orphan finding already covers it

        if (sidecar.ImporterUnknown)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, sidecar.Path,
                $"names importer '{sidecar.ImporterName}', which this chain does not have (it has: {ImporterChain.Resolution.Known(importers)}) — a game's own importer missing from the list, or a typo"));
            return;
        }

        if (sidecar.Importer is not null) return;

        var candidate = new ImportCandidate(fileSystem, new AssetProjectLayout(assetsRoot.GetDirectory()), sidecar.Asset, sidecar);
        if (ImporterChain.Claim(importers, candidate) is { } claimant)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Warning, sidecar.Path,
                $"names no importer; '{claimant.Name}' claims it — run `paradise assets verify --fix` (or `watch`) to record that"));
        }
    }

    /// <summary>A GLB that was never extracted has nothing for the build: the mesh, materials and clips that ship are the extracted ones.</summary>
    private static void VerifyExtracted(IFileSystem fileSystem, UPath path, List<VerifyFinding> findings)
    {
        var sidecar = SidecarMeta.PathFor(path);
        if (!fileSystem.FileExists(sidecar)) return;   // the missing-sidecar finding already covers it
        if (!MeshContainer.HasGeometry(path, fileSystem.ReadAllBytes(path))) return;
        try
        {
            if (GlbImportSettings.ReadExtraction(SidecarMeta.Load(fileSystem, sidecar)).Extracted) return;
        }
        catch (SidecarMetaException)
        {
            return;   // reported against the sidecar
        }

        findings.Add(new VerifyFinding(
            VerifySeverity.Warning, path,
            "has not been extracted, so nothing of it ships — run `paradise assets extract` on it to produce its mesh, materials and clips"));
    }

    private static void VerifyMaterial(IFileSystem fileSystem, UPath path, List<VerifyFinding> findings)
    {
        try
        {
            MaterialDocument.Load(fileSystem, path);
        }
        catch (FormatException failure)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, failure.Message));
        }
    }

    private static void VerifyDocument(
        IFileSystem fileSystem, AssetIndex sources, UPath path, List<VerifyFinding> findings)
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

        foreach (var problem in MalformedReferences(path, document))
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, problem));
        }

        VerifyInstances(fileSystem, sources, path, document, findings);
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
        IFileSystem fileSystem, AssetIndex sources, UPath path, PrefabDocument document, List<VerifyFinding> findings)
    {
        if (!document.Objects.Any(o => o.Prefab is not null || o.Target is not null)) return;

        var result = PrefabResolver.Resolve(document, reference =>
        {
            try
            {
                var resolution = sources.Resolve(reference);
                return resolution.Found ? PrefabDocumentSerializer.Load(fileSystem, resolution.Asset) : null;
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

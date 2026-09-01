using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>How bad a <see cref="VerifyFinding"/> is.</summary>
public enum VerifySeverity
{
    /// <summary>Suspicious but buildable — reported, does not fail the verb.</summary>
    Warning,

    /// <summary>The source tree is inconsistent; <c>verify</c> fails.</summary>
    Error,
}

/// <summary>One thing <c>verify</c> found, tied to the path it is about.</summary>
/// <param name="Severity">Whether this fails the verb.</param>
/// <param name="Path">The file the finding is about.</param>
/// <param name="Message">What is wrong, phrased for the person who must fix it.</param>
public readonly record struct VerifyFinding(VerifySeverity Severity, UPath Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{(Severity == VerifySeverity.Error ? "error" : "warning")}: {Path}: {Message}";
}

/// <summary>
/// The <c>verify</c> verb: walks <c>assets/</c> and reports everything inconsistent about it.
/// </summary>
/// <remarks>
/// <para>
/// This is the CI gate for the source tree itself (the built tree has its own checks against
/// the manifest). Everything here is an invariant some other part of the design relies on:
/// sidecar GUIDs must be unique or references re-linked by GUID become ambiguous; sidecars must
/// pair with assets or <c>mv</c> half-happened; documents must parse strictly or the build
/// fails later with less context; documents must be canonical or the next machine rewrite
/// produces a noise diff a human has to review.
/// </para>
/// <para>
/// Verification never mutates the tree. Minting missing sidecars is a decision (<c>mv</c>/import
/// tooling), not a side effect of checking.
/// </para>
/// </remarks>
public static class ProjectVerifier
{
    /// <summary>Verifies the project's source tree and returns the findings, errors first.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    public static IReadOnlyList<VerifyFinding> Verify(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var findings = new List<VerifyFinding>();
        if (!fileSystem.DirectoryExists(layout.Assets))
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, layout.Assets, "the assets directory does not exist"));
            return findings;
        }

        VerifyManifest(fileSystem, layout, findings);

        var guids = new Dictionary<Guid, UPath>();
        foreach (var path in fileSystem.EnumerateFiles(layout.Assets, "*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.Ordinal))
        {
            var assetClass = AssetClassifier.Classify(layout.Assets, path);

            // Every asset carries an identity, and identity lives in ONE place -- the sidecar --
            // whether the asset is a GLB or a scene document.
            if (AssetClassifier.NeedsSidecar(assetClass)
                && !fileSystem.FileExists(SidecarMeta.PathFor(path)))
            {
                findings.Add(new VerifyFinding(
                    VerifySeverity.Error, path,
                    "has no sidecar — mint one so the asset has an identity (tooling owns sidecars; see the mv/import verbs)"));
            }

            switch (assetClass)
            {
                case AssetClass.Sidecar:
                    VerifySidecar(fileSystem, layout.Assets, path, guids, findings);
                    break;

                case AssetClass.Prefab:
                    VerifyDocument(fileSystem, layout, path, findings);
                    break;

                // No "the pipeline does not know this file" warning: verify cannot tell. An
                // importer claims an asset inside its own Import, on whatever grounds it likes,
                // so the only truthful answer comes from running the chain — and a declined
                // asset means "not mine" OR "not for this tree", which even the build cannot
                // separate. A file nobody builds is still caught by the sidecar rule above,
                // which is the check that actually matters: everything under assets/ is an
                // asset, whether or not a step processes it.
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

        // Settings are opaque to the format, so this is where they meet the registry of steps
        // that actually read them. An unknown domain is a WARNING — it may be a typo, or a
        // sidecar written by a newer pipeline — but a malformed KNOWN domain is an error, because
        // the build would refuse it with less context.
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

    /// <summary>
    /// One document, whatever a game calls it.
    /// </summary>
    /// <remarks>
    /// This used to be two methods that checked overlapping halves of the same file: a scene got
    /// canonical form, references and instances but no root rule, and a prefab got the root rule
    /// and references but neither of the other two. Nothing justified the split -- a prefab that
    /// drifted out of canonical form was as much a diff-noise problem as a level that did, and a
    /// prefab holding a broken instance simply went unchecked.
    /// </remarks>
    private static void VerifyDocument(IFileSystem fileSystem, AssetProjectLayout layout, UPath path, List<VerifyFinding> findings)
    {
        PrefabDocument document;
        try
        {
            // Load validates the single-root rule, so it is checked here for every document.
            document = PrefabDocumentSerializer.Load(fileSystem, path);
        }
        catch (PrefabDocumentException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
            return;
        }

        // The canonical-form drift guard (the prefab-check half of the parity story): a document a
        // tool wrote is byte-canonical, so a difference means a hand edit — legal, but the next
        // machine write will reformat it, and that diff belongs to this commit, not that one.
        var canonical = PrefabDocumentSerializer.Write(document);
        if (fileSystem.ReadAllText(path) != canonical)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Warning, path,
                "is valid but not in canonical form; rewrite it (prefab-check --fix) so machine edits stay out of your diffs"));
        }

        VerifyReferences(fileSystem, layout, path, document, findings);
        VerifyInstances(fileSystem, layout, path, document, findings);
    }

    /// <summary>
    /// Every asset reference in a document: the two halves must agree, and the asset must exist.
    /// </summary>
    /// <remarks>
    /// Carrying a guid AND a path is only worth anything if something checks they still name the
    /// same asset. Without this a half-finished move — the path updated, the guid stale, or the
    /// reverse — resolves to whichever half the resolver happens to prefer, which is exactly the
    /// silent wrong-asset failure the pair was introduced to prevent.
    /// </remarks>
    private static void VerifyReferences(
        IFileSystem fileSystem, AssetProjectLayout layout, UPath path, PrefabDocument document, List<VerifyFinding> findings)
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

        // The same walk PrefabBake.ToValue does, because that is the specification: a reference
        // the bake will flatten is a reference verify must have checked, and the shape the pair
        // exists for — material slots — is an ARRAY of references, not a value-position one.
        void Walk(object? value, string where)
        {
            switch (value)
            {
                // Gated on the format's OWN definition of a reference rather than on the model
                // type: inside an array the reader wraps every table as inline, so an arbitrary
                // payload table would otherwise be read as a malformed reference and reported as
                // one. The empty table is a reference to nothing, which is always consistent.
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
            var target = layout.Assets / reference.Path;
            if (!fileSystem.FileExists(target))
            {
                findings.Add(new VerifyFinding(
                    VerifySeverity.Error, path,
                    $"references '{reference.Path}' in {where}, which does not exist under assets/"));
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

    /// <summary>The asset's own identity: a sidecar for a binary, the document itself otherwise.</summary>
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

    /// <summary>Every prefab instance in a scene must actually resolve.</summary>
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

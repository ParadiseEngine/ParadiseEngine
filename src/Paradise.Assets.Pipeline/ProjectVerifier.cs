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
            switch (AssetClassifier.Classify(layout.Assets, path))
            {
                case AssetClass.Sidecar:
                    VerifySidecar(fileSystem, path, guids, findings);
                    break;

                case AssetClass.Foreign:
                    if (!fileSystem.FileExists(SidecarMeta.PathFor(path)))
                    {
                        findings.Add(new VerifyFinding(
                            VerifySeverity.Error, path,
                            "has no sidecar — mint one so the asset has an identity (tooling owns sidecars; see the mv/import verbs)"));
                    }

                    break;

                case AssetClass.Scene:
                    VerifyScene(fileSystem, path, findings);
                    break;

                case AssetClass.Other:
                    findings.Add(new VerifyFinding(
                        VerifySeverity.Warning, path,
                        "is not a kind of file the pipeline knows; it will be ignored by every build step"));
                    break;
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

    private static void VerifySidecar(IFileSystem fileSystem, UPath path, Dictionary<Guid, UPath> guids, List<VerifyFinding> findings)
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

        if (AssetClassifier.TryGetForeignKind(asset, out var expected) && expected != meta.Kind)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Error, path,
                $"declares kind '{meta.Kind}' but '{asset.GetName()}' is a {expected} asset"));
        }
    }

    private static void VerifyScene(IFileSystem fileSystem, UPath path, List<VerifyFinding> findings)
    {
        SceneDocument document;
        try
        {
            document = SceneDocumentSerializer.Load(fileSystem, path);
        }
        catch (SceneDocumentException error)
        {
            findings.Add(new VerifyFinding(VerifySeverity.Error, path, error.Message));
            return;
        }

        // The canonical-form drift guard (the scene-check half of the parity story): a scene a
        // tool wrote is byte-canonical, so a difference means a hand edit — legal, but the next
        // machine write will reformat it, and that diff belongs to this commit, not that one.
        var canonical = SceneDocumentSerializer.Write(document);
        if (fileSystem.ReadAllText(path) != canonical)
        {
            findings.Add(new VerifyFinding(
                VerifySeverity.Warning, path,
                "is valid but not in canonical form; rewrite it (scene-check --fix) so machine edits stay out of your diffs"));
        }
    }
}

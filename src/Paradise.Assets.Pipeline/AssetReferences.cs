using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What every reference-aware verb hands an importer: the tree as one scan, and whether the asset's own bytes may be written.</summary>
/// <param name="RewriteSources">
/// Whether <see cref="IAssetImporter.Rewrite"/> may write the asset's own bytes (a document's
/// paths, a container's uris) as well as its sidecar. False at build time: a reconcile there
/// records identities, and never moves a uri under an author's feet.
/// </param>
public sealed record ReferenceContext(
    IFileSystem FileSystem,
    AssetProjectLayout Layout,
    AssetIndex Index,
    AssetIgnoreRules Ignore,
    bool RewriteSources = true)
{
    public AssetClass Classify(UPath asset) => AssetClassifier.Classify(Layout.Assets, asset, Ignore);

    public string Relative(UPath asset) => Index.Relative(asset);
}

/// <summary>One place an asset refers to another.</summary>
/// <param name="Where">The site as <c>verify</c> names it: a document field (<c>game.Mesh.Slots[0]</c>, <c>prefab</c>) or a container slot (<c>images[0]</c>).</param>
/// <param name="Reference">The identity, or null for a site that carries only a path (a container uri nothing has recorded yet).</param>
/// <param name="Hint">The assets-relative path the site SPELLS — the reference's path half, or a uri resolved against the asset's directory; null when it cannot even be placed under <c>assets/</c>.</param>
/// <param name="Spelled">The text as the file spells it — a path half, or a container-relative uri — for messages that quote the file.</param>
/// <param name="Note">Why a path-only site is one, for the finding that asks for it to be recorded (a re-export that changed the uri, say).</param>
public readonly record struct ReferenceSite(string Where, AssetReference? Reference, string? Hint, string Spelled, string? Note = null);

/// <summary>An asset's references as its importer reads them, or why it could not.</summary>
/// <param name="Problem">Non-null when the asset would not parse; the sites are then empty and the message is what <c>verify</c> already reports against the asset.</param>
public sealed record AssetReferences(IReadOnlyList<ReferenceSite> Sites, string? Problem = null)
{
    public static AssetReferences Unreadable(string problem) => new([], problem);
}

/// <summary>The importer chain asked about references, walked the way a build walks it: last appended wins.</summary>
public static class ReferenceChain
{
    /// <summary>The importer that claims <paramref name="asset"/> and what it read; null when none does.</summary>
    public static (IAssetImporter Importer, AssetReferences References)? Claim(
        IReadOnlyList<IAssetImporter> importers, ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(context);

        for (var i = importers.Count - 1; i >= 0; i--)
        {
            if (importers[i].References(context, asset) is { } references) return (importers[i], references);
        }

        return null;
    }

    /// <summary>Brings <paramref name="asset"/>'s references in line with the tree through whichever importer claims it; null when none does or nothing changed.</summary>
    public static RepairedDocument? Rewrite(IReadOnlyList<IAssetImporter> importers, ReferenceContext context, UPath asset)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(context);

        for (var i = importers.Count - 1; i >= 0; i--)
        {
            if (importers[i].References(context, asset) is null) continue;
            return importers[i].Rewrite(context, asset);
        }

        return null;
    }

    /// <summary>
    /// The findings every reference site yields, by the one rule: the guid decides, the path is a
    /// hint. Derived here rather than reported by each importer, so an importer cannot forget one.
    /// </summary>
    public static IEnumerable<VerifyFinding> Verify(ReferenceContext context, UPath asset, AssetReferences references)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(references);

        var index = context.Index;
        foreach (var site in references.Sites)
        {
            if (site.Reference is not { } reference)
            {
                yield return PathOnly(context, asset, site);
                continue;
            }

            var resolution = index.Resolve(reference);
            var guid = DocumentGuid.Format(reference.Guid);
            switch (resolution.Status)
            {
                case ReferenceStatus.Resolved when site.Hint == resolution.Path:
                    break;

                case ReferenceStatus.Resolved or ReferenceStatus.Stale:
                    // Not an error: the guid resolved it, so the build is correct and only the text
                    // is out of date — a rename in Finder, or a sidecar the maintainer relinked.
                    yield return new VerifyFinding(VerifySeverity.Warning, asset, Stale(site, resolution, guid));
                    break;

                // The hint names an asset whose own identity could not be read; that sidecar
                // carries its own finding, and repeating it per reference buries it.
                case ReferenceStatus.Undetermined:
                    break;

                default:
                    yield return new VerifyFinding(VerifySeverity.Error, asset, Unresolved(index, site, resolution, guid));
                    break;
            }
        }
    }

    private static VerifyFinding PathOnly(ReferenceContext context, UPath asset, ReferenceSite site)
    {
        var index = context.Index;
        if (site.Hint is null)
        {
            return new VerifyFinding(VerifySeverity.Error, asset, $"in {site.Where}, references '{site.Spelled}', which resolves outside assets/");
        }

        var target = index.Root / site.Hint;
        if (index.IdentityOf(target) is null)
        {
            var problem = index.Problem(target, site.Spelled) ?? $"references '{site.Spelled}', which has no identity to record";
            return new VerifyFinding(VerifySeverity.Error, asset, $"in {site.Where}, {problem}");
        }

        var why = site.Note is null ? "has no identity recorded, so a rename would break it" : site.Note;
        return new VerifyFinding(
            VerifySeverity.Warning, asset,
            $"in {site.Where}, '{site.Spelled}' {why} — run `paradise assets verify --fix` (or `watch`) to record it");
    }

    /// <summary>
    /// The guid resolved the reference and only its path text is out of date. A path that names
    /// a DIFFERENT real asset is called out by that asset's guid: a Finder rename and a hand edit
    /// that changed only the path look identical here, and <c>--fix</c> reverts the second one,
    /// so the message has to say which guid to change if the path was the intended half.
    /// </summary>
    private static string Stale(ReferenceSite site, ReferenceResolution resolution, string guid)
    {
        var message = $"in {site.Where}, the path half says '{site.Spelled}' but guid '{guid}' names " +
            $"'{resolution.Path}'; the guid resolves it, so this builds — run " +
            "`paradise assets verify --fix` to catch the path up";

        if (resolution.HintIdentity is { } other && other != resolution.Reference.Guid)
        {
            message += $". Note '{site.Hint}' exists and is '{DocumentGuid.Format(other)}': " +
                "if THAT is the asset meant here, change the guid instead, since --fix keeps the guid";
        }

        return message;
    }

    /// <summary>The guid names no asset in the tree, so the reference resolves to nothing; what the PATH half names decides how to say so.</summary>
    private static string Unresolved(AssetIndex index, ReferenceSite site, ReferenceResolution resolution, string guid)
    {
        var path = site.Hint ?? resolution.Reference.Path;

        if (resolution.Asset.IsNull)
        {
            return $"in {site.Where}, references guid '{guid}', which no asset under assets/ carries, and " +
                $"'{path}' does not name a place under assets/ either";
        }

        if (index.IsIgnored(resolution.Asset))
        {
            return $"in {site.Where}, references guid '{guid}', which no asset under assets/ carries; " +
                $"'{path}' exists but is ignored by the manifest, so it has no identity to " +
                "reference. Un-ignore it, or point the reference at an asset the build owns";
        }

        if (resolution.HintIdentity is { } other)
        {
            return $"in {site.Where}, references guid '{guid}', which no asset under assets/ carries; " +
                $"'{path}' exists but is '{DocumentGuid.Format(other)}'. Re-point the " +
                "reference, or restore the asset whose identity this is";
        }

        if (index.Problem(resolution.Asset, path) is { } problem)
        {
            return $"in {site.Where}, {problem}, and no asset under assets/ carries its guid '{guid}'";
        }

        return $"in {site.Where}, references guid '{guid}', which no asset under assets/ carries " +
            $"('{path}' exists but has no readable identity)";
    }
}

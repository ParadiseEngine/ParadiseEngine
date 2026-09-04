using Paradise.Assets.Documents;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What a reconcile found a mesh's references should be, and what it would take to get there.</summary>
/// <param name="Recorded">The sidecar's entries before.</param>
/// <param name="References">The entries the sidecar should hold now.</param>
/// <param name="UriBySlot">The slots whose uri should change and what to, for a format that can be rewritten; the entries in <see cref="References"/> still spell what the container spells until it does.</param>
/// <param name="Unresolved">Slots whose uri names nothing identified: not recorded, and <c>verify</c>'s finding.</param>
/// <param name="Changes">One line per entry recorded, re-resolved or caught up, for the verb to print.</param>
public sealed record MeshReconciliation(
    IReadOnlyList<MeshReference> Recorded,
    IReadOnlyList<MeshReference> References,
    IReadOnlyDictionary<string, string> UriBySlot,
    IReadOnlyList<ContainerReference> Unresolved,
    IReadOnlyList<string> Changes)
{
    public bool SidecarChanged => !Recorded.SequenceEqual(References);
}

/// <summary>
/// Keeps a mesh's <c>[mesh]</c> sidecar entries in step with the container and the tree: the
/// one rule for "the container says this uri, the sidecar says this guid — which wins?".
/// </summary>
/// <remarks>
/// The identity wins when the container still spells the uri the entry was recorded from: the
/// texture moved, the guid still names it, and the uri is caught up. The uri wins when it differs
/// from the recorded one: the author re-exported with a different path in the DCC, which is the
/// one edit that can only be made through the uri, so it is re-resolved from scratch. A slot the
/// container no longer has loses its entry.
/// </remarks>
public static class MeshReferences
{
    public static MeshReconciliation Reconcile(IFileSystem fileSystem, AssetIndex index, UPath container)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);

        var relative = index.Relative(container);
        var recorded = Recorded(fileSystem, container);
        var bySlot = MeshImportSettings.BySlot(recorded);

        var references = new List<MeshReference>();
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new List<ContainerReference>();
        var changes = new List<string>();

        foreach (var named in MeshContainer.Read(container, fileSystem.ReadAllBytes(container)))
        {
            if (bySlot.TryGetValue(named.Slot, out var entry) && MeshContainer.SameUri(entry.Uri, named.Uri))
            {
                var resolution = index.Resolve(entry.Reference);
                if (!resolution.Found)
                {
                    references.Add(entry);   // verify names it; dropping it would lose the evidence
                    continue;
                }

                // The entry keeps the uri the container SPELLS. Recording the desired one here
                // would, on a sidecar-only pass, leave sidecar and container disagreeing, and the
                // next pass would read that as a re-export and drop the identity (review of #244).
                // Apply substitutes it only once the container really says it.
                var expected = MeshContainer.UriFor(relative, resolution.Path);
                if (!MeshContainer.SameUri(expected, named.Uri))
                {
                    changes.Add($"{named.Slot}: {entry.Reference.Path} -> {resolution.Path}");
                    uris[named.Slot] = expected;
                }

                references.Add(new MeshReference(named.Slot, named.Uri, resolution.Current));
                continue;
            }

            if (MeshContainer.AssetPathFor(relative, named.Uri) is { } assetPath
                && index.IdentityOf(index.Root / assetPath) is { } guid)
            {
                changes.Add(bySlot.ContainsKey(named.Slot)
                    ? $"{named.Slot}: re-exported as {named.Uri}, now {assetPath}"
                    : $"{named.Slot}: {named.Uri} recorded as {assetPath}");
                references.Add(new MeshReference(named.Slot, named.Uri, new AssetReference(guid, assetPath)));
                continue;
            }

            unresolved.Add(named);
        }

        return new MeshReconciliation(recorded, references, uris, unresolved, changes);
    }

    /// <summary>Writes the sidecar when its entries changed and, when asked and the format allows, the container's uris; null when nothing was written.</summary>
    public static RepairedDocument? Apply(IFileSystem fileSystem, UPath container, MeshReconciliation reconciliation, bool rewriteContainer)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(reconciliation);

        var written = false;
        var entries = reconciliation.References;

        if (rewriteContainer && MeshContainer.CanRewrite(container) && reconciliation.UriBySlot.Count > 0)
        {
            var bytes = fileSystem.ReadAllBytes(container);
            var rewritten = MeshContainer.RewriteUris(container, bytes, reconciliation.UriBySlot);
            if (!ReferenceEquals(rewritten, bytes))
            {
                fileSystem.WriteAllBytes(container, rewritten);
                written = true;
                // Only now does the container spell the new uri, so only now may the entries.
                entries = entries
                    .Select(entry => reconciliation.UriBySlot.TryGetValue(entry.Slot, out var uri) ? entry with { Uri = uri } : entry)
                    .ToList();
            }
        }

        if (!entries.SequenceEqual(reconciliation.Recorded) && fileSystem.FileExists(SidecarMeta.PathFor(container)))
        {
            var meta = SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(container));
            MeshImportSettings.Write(meta, entries);
            meta.Save(fileSystem, SidecarMeta.PathFor(container));
            written = true;
        }

        return written ? new RepairedDocument(container, reconciliation.Changes) : null;
    }

    /// <summary>The sidecar's entries, or none when the mesh has no readable sidecar yet.</summary>
    public static IReadOnlyList<MeshReference> Recorded(IFileSystem fileSystem, UPath container)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var sidecar = SidecarMeta.PathFor(container);
        if (!fileSystem.FileExists(sidecar)) return [];
        try
        {
            return MeshImportSettings.Read(SidecarMeta.Load(fileSystem, sidecar));
        }
        catch (SidecarMetaException)
        {
            return [];
        }
    }
}

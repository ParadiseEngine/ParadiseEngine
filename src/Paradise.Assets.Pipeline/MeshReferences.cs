using Paradise.Assets.Documents;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What a reconcile found a mesh's references should be, and what it would take to get there.</summary>
/// <param name="Recorded">The sidecar's entries before.</param>
/// <param name="References">The entries the sidecar should hold now.</param>
/// <param name="UriBySlot">The uri each slot should spell from where the container sits, for a format that can be rewritten.</param>
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
        var bySlot = recorded.ToDictionary(entry => entry.Slot, StringComparer.Ordinal);

        var references = new List<MeshReference>();
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new List<ContainerReference>();
        var changes = new List<string>();

        foreach (var named in MeshContainer.Read(container, fileSystem.ReadAllBytes(container)))
        {
            if (bySlot.TryGetValue(named.Slot, out var entry) && entry.Uri == named.Uri)
            {
                var resolution = index.Resolve(entry.Reference);
                if (!resolution.Found)
                {
                    references.Add(entry);   // verify names it; dropping it would lose the evidence
                    continue;
                }

                var expected = MeshContainer.UriFor(relative, resolution.Path);
                if (resolution.Status == ReferenceStatus.Stale || expected != named.Uri)
                {
                    changes.Add($"{named.Slot}: {entry.Reference.Path} -> {resolution.Path}");
                }

                references.Add(new MeshReference(named.Slot, expected, resolution.Current));
                uris[named.Slot] = expected;
                continue;
            }

            if (MeshContainer.AssetPathFor(relative, named.Uri) is { } assetPath
                && index.IdentityOf(index.Root / assetPath) is { } guid)
            {
                changes.Add(bySlot.ContainsKey(named.Slot)
                    ? $"{named.Slot}: re-exported as {named.Uri}, now {assetPath}"
                    : $"{named.Slot}: {named.Uri} recorded as {assetPath}");
                references.Add(new MeshReference(named.Slot, named.Uri, new AssetReference(guid, assetPath)));
                uris[named.Slot] = named.Uri;
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
        if (reconciliation.SidecarChanged && fileSystem.FileExists(SidecarMeta.PathFor(container)))
        {
            var meta = SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(container));
            MeshImportSettings.Write(meta, reconciliation.References);
            meta.Save(fileSystem, SidecarMeta.PathFor(container));
            written = true;
        }

        if (rewriteContainer && MeshContainer.CanRewrite(container))
        {
            var bytes = fileSystem.ReadAllBytes(container);
            var rewritten = MeshContainer.RewriteUris(container, bytes, reconciliation.UriBySlot);
            if (!ReferenceEquals(rewritten, bytes))
            {
                fileSystem.WriteAllBytes(container, rewritten);
                written = true;
            }
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

using Paradise.Assets.Documents;
using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What a reconcile found a mesh's references should be.</summary>
/// <param name="Recorded">The sidecar's entries before.</param>
/// <param name="References">The entries the sidecar should hold now.</param>
/// <param name="Unresolved">Slots whose uri names nothing identified: not recorded, and <c>verify</c>'s finding.</param>
/// <param name="Changes">One line per entry recorded, re-resolved or caught up, for the verb to print.</param>
public sealed record MeshReconciliation(
    IReadOnlyList<MeshReference> Recorded,
    IReadOnlyList<MeshReference> References,
    IReadOnlyList<ContainerReference> Unresolved,
    IReadOnlyList<string> Changes)
{
    public bool SidecarChanged => !Recorded.SequenceEqual(References);
}

/// <summary>
/// Keeps a mesh's <c>[glb]</c> sidecar entries in step with the container and the tree: the
/// one rule for "the container says this uri, the sidecar says this guid — which wins?". Only the
/// sidecar is written; the container is the DCC's.
/// </summary>
/// <remarks>
/// The identity wins when the container still spells the uri the entry was recorded from: the
/// file moved, the guid still names it, and the entry's path is caught up while its uri stays the
/// one the container spells. The uri wins when it differs from the recorded one: the author
/// re-exported with a different path in the DCC, which is the one edit that can only be made
/// through the uri, so it is re-resolved from scratch. A slot the container no longer has loses its entry.
/// </remarks>
public static class MeshReferences
{
    public static MeshReconciliation Reconcile(IFileSystem fileSystem, AssetIndex index, UPath container)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(index);

        var relative = index.Relative(container);
        var recorded = Recorded(fileSystem, container);
        var bySlot = GlbImportSettings.BySlot(recorded);

        var references = new List<MeshReference>();
        var unresolved = new List<ContainerReference>();
        var changes = new List<string>();

        foreach (var named in MeshContainer.Read(fileSystem, container))
        {
            if (bySlot.TryGetValue(named.Slot, out var entry) && MeshContainer.SameUri(entry.Uri, named.Uri))
            {
                var resolution = index.Resolve(entry.Reference);
                if (!resolution.Found)
                {
                    references.Add(entry);   // verify names it; dropping it would lose the evidence
                    continue;
                }

                // The entry keeps the uri the container SPELLS: that is what tells the next pass
                // this slot was not re-exported, so the identity keeps winning.
                var current = resolution.Current;
                if (current != entry.Reference) changes.Add($"{named.Slot}: {entry.Reference.Path} -> {current.Path}");
                references.Add(new MeshReference(named.Slot, named.Uri, current));
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

        return new MeshReconciliation(recorded, references, unresolved, changes);
    }

    /// <summary>Writes the sidecar when its entries changed; null when nothing was written.</summary>
    public static RepairedDocument? Apply(IFileSystem fileSystem, UPath container, MeshReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(reconciliation);

        if (!reconciliation.SidecarChanged || !fileSystem.FileExists(SidecarMeta.PathFor(container))) return null;

        var meta = SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(container));
        GlbImportSettings.Write(meta, reconciliation.References);
        meta.Save(fileSystem, SidecarMeta.PathFor(container));
        return new RepairedDocument(container, reconciliation.Changes);
    }

    /// <summary>The sidecar's entries, or none when the mesh has no readable sidecar yet.</summary>
    public static IReadOnlyList<MeshReference> Recorded(IFileSystem fileSystem, UPath container)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var sidecar = SidecarMeta.PathFor(container);
        if (!fileSystem.FileExists(sidecar)) return [];
        try
        {
            return GlbImportSettings.Read(SidecarMeta.Load(fileSystem, sidecar));
        }
        catch (SidecarMetaException)
        {
            return [];
        }
    }
}

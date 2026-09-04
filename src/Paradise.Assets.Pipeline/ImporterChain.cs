using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>What a claim may look at: the asset, its sidecar, and its bytes. Nothing to write into, and no profile — a claim is "is this mine?", not an import.</summary>
public sealed record ImportCandidate(IFileSystem FileSystem, AssetProjectLayout Layout, UPath Asset, AssetSidecar? Meta)
{
    /// <summary>Assets-relative, '/'-separated.</summary>
    public string Source => Asset.FullName[(Layout.Assets.FullName.Length + 1)..];

    /// <summary>Case-insensitive, with dot.</summary>
    public bool HasExtension(params ReadOnlySpan<string> extensions)
    {
        var extension = Asset.GetExtensionWithDot() ?? string.Empty;
        foreach (var claimed in extensions)
        {
            if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public bool IsManifest => Source == AssetProjectLayout.ManifestFileName;
}

/// <summary>
/// The one place the importer list is walked. An asset's importer is the one its sidecar NAMES;
/// the chain is asked only to decide that name (at mint, or for a sidecar from before the field
/// existed), last appended first so a game's importer shadows the built-in it replaces.
/// </summary>
/// <remarks>
/// Recording the choice is what lets an author pick a different importer for one asset by editing
/// one line, and what keeps a build from re-deciding under them. It is decided when the sidecar is
/// minted, not at the first build, because a build that edits committed sidecars is the dirty-tree
/// failure the recorded hash already taught.
/// </remarks>
public static class ImporterChain
{
    /// <summary>The importer that claims <paramref name="candidate"/>, or null when none does.</summary>
    public static IAssetImporter? Claim(IReadOnlyList<IAssetImporter> importers, ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(candidate);

        for (var i = importers.Count - 1; i >= 0; i--)
        {
            if (importers[i].Claims(candidate)) return importers[i];
        }

        return null;
    }

    /// <summary>The importer named <paramref name="name"/>, or null. Ordinal: a name is an identifier, not prose.</summary>
    public static IAssetImporter? Named(IReadOnlyList<IAssetImporter> importers, string name)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(name);

        for (var i = importers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(importers[i].Name, name, StringComparison.Ordinal)) return importers[i];
        }

        return null;
    }

    /// <summary>
    /// The importer for <paramref name="candidate"/>: the one its sidecar names when the chain has
    /// it, else the claim. A recorded name the chain lacks is <see cref="Resolution.Unknown"/> —
    /// a game's own importer missing from the list passed to <c>BuildHost.Run</c> is the usual
    /// cause — and is never silently replaced by a claim.
    /// </summary>
    public static Resolution For(IReadOnlyList<IAssetImporter> importers, ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Meta?.ImporterName is { } recorded)
        {
            // Resolved once, at load; an unknown name stays unknown rather than falling to a claim.
            return new Resolution(candidate.Meta.Importer, recorded, Recorded: true);
        }

        var claimed = Claim(importers, candidate);
        return new Resolution(claimed, claimed?.Name, Recorded: false);
    }

    /// <summary>What <see cref="For"/> found.</summary>
    /// <param name="Importer">The importer to use; null when nothing claims the asset, or when the recorded name is unknown to the chain.</param>
    /// <param name="Name">The recorded name, or the claimant's; null when neither exists.</param>
    /// <param name="Recorded">Whether the sidecar named it. With a null importer this means the name is unknown.</param>
    public readonly record struct Resolution(IAssetImporter? Importer, string? Name, bool Recorded)
    {
        public bool Unknown => Recorded && Importer is null;

        /// <summary>The chain's names, for a message that says what a sidecar may name.</summary>
        public static string Known(IReadOnlyList<IAssetImporter> importers) => string.Join(", ", importers.Select(importer => importer.Name));
    }
}

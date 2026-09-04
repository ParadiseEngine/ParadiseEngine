using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Everything an importer may draw on.</summary>
/// <remarks>
/// <paramref name="FileSystem"/> is the source tree as the build index sees it: every file read
/// or asked about through it is recorded, and the asset is rebuilt when any of them changes. It
/// is read-only, case-exact under <c>assets/</c>, and refuses directory listings. Anything the
/// output depends on that is NOT read through it — a tool's version, the profile's settings — is
/// the runner's to fold into the index environment; the built-ins have nothing else.
/// <paramref name="Meta"/> is the asset's sidecar, which verify guarantees exists before a build
/// runs. <paramref name="Sources"/> also resolves an <see cref="Paradise.Authoring.AssetReference"/>
/// the asset makes: the guid decides, the path half is a hint a rename can leave stale.
/// </remarks>
public sealed record ImportContext(
    IFileSystem FileSystem,
    AssetIndex Sources,
    UPath Asset,
    string Source,
    AssetSidecar? Meta,
    BuildProfile Profile,
    ProjectOutputTarget Target,
    IFileSystem Output,
    ArtifactCache Cache,
    ITextureEncoder? Encoder,
    ILogger Log)
{
    public UPath AssetsRoot => Sources.Root;

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

    /// <summary>Resolves an authored reference — the guid decides — and records the dependency it creates.</summary>
    /// <remarks>
    /// The recorded input is the referenced asset's SIDECAR, not the asset. What this output
    /// depends on is WHERE that guid lives, and the sidecar beside the asset is the record of
    /// that: reading it here is what makes a rename of the referenced asset rebuild this one.
    /// Depending on the asset's own bytes instead would miss a move (the bytes are identical at
    /// the new path) and rebuild on every unrelated edit to it.
    /// </remarks>
    public ReferenceResolution Resolve(Paradise.Authoring.AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolution = Sources.Resolve(reference);
        if (!resolution.Found) return resolution;

        var sidecar = SidecarMeta.PathFor(resolution.Asset);
        if (FileSystem.FileExists(sidecar)) FileSystem.ReadAllBytes(sidecar);

        return resolution;
    }

    /// <summary>
    /// Resolves a path a FILE FORMAT carries — a GLB's image uri — against the real tree and
    /// records the dependency; returns the error to report, or null when it resolves.
    /// </summary>
    /// <remarks>
    /// Path-only on purpose, and the one place that is right: these live inside a container the
    /// DCC wrote and carry no identity, so there is no guid to prefer. An authored reference goes
    /// through <see cref="Resolve"/> instead.
    /// </remarks>
    public string? CheckReference(string reference, out UPath resolved)
    {
        ArgumentNullException.ThrowIfNull(reference);

        resolved = (Asset.GetDirectory() / Uri.UnescapeDataString(reference)).ToAbsolute();
        var problem = Sources.Problem(resolved, reference);
        // Through the observed filesystem, so the index rebuilds this asset when the file it
        // references appears or disappears.
        FileSystem.FileExists(resolved);
        return problem is null ? null : $"{Source}: {problem}";
    }
}

/// <summary>One link in the import chain: handle the asset or decline and let the next link try.</summary>
/// <remarks>
/// Importers claim inside <see cref="Import"/> rather than declaring extensions, so a project can
/// append one that shadows a built-in on whatever grounds it likes; the chain is a plain list a
/// game's own host passes to <c>BuildHost.Run</c> (issue #208). Decline first, validate next,
/// write last: the chain shares one output mount, so an early write lands in the manifest under
/// whoever ends up handling the asset, or survives in a tree the failed build already declared
/// suspect. Read every input through <see cref="ImportContext.FileSystem"/> and nothing else;
/// the build index reuses the output whenever everything read there is unchanged.
/// </remarks>
public interface IAssetImporter
{
    string Name { get; }

    /// <summary>False for outputs addressed by path alone (a config).</summary>
    bool RecordsIdentity { get; }

    /// <summary>
    /// Whether this importer handles the asset — a path, at most a header of the bytes. The one
    /// claim point: the chain asks it once, when the sidecar is minted, and records the answer;
    /// nothing else searches. Abstract on purpose: an importer that cannot say whether an asset is
    /// its own cannot be recorded for one.
    /// </summary>
    bool Claims(ImportCandidate candidate);

    /// <summary>
    /// Imports an asset the sidecar names this importer for. A failure is reported through
    /// <paramref name="errors"/>, prefixed with the source, and writes nothing. Returning false
    /// says the asset is not what it claimed to be — kept as a guard, since a hand-edited
    /// <c>importer</c> line can name this importer for anything, and the build reports it.
    /// </summary>
    bool Import(ImportContext context, List<string> errors);

    /// <summary>
    /// Every reference <paramref name="asset"/> holds — from its bytes and from its sidecar.
    /// <see cref="AssetReferences.None"/> for a kind that holds none (the default), never null:
    /// the graph, <c>mv</c>, <c>rm</c>, <c>refs</c>, <c>verify</c> and the watcher iterate what
    /// comes back. Which importer is asked is the sidecar's to say, not this method's.
    /// </summary>
    AssetReferences References(ReferenceContext context, UPath asset) => AssetReferences.None;

    /// <summary>
    /// Brings the asset's references in line with the tree — its sidecar's entries, and its own
    /// bytes when <see cref="ReferenceContext.RewriteSources"/> allows — through the one rule: the
    /// guid decides, the path is a hint. Null when nothing changed. Called only after
    /// <see cref="References"/> claimed the asset.
    /// </summary>
    RepairedDocument? Rewrite(ReferenceContext context, UPath asset) => null;

    /// <summary>The sidecar settings domains this importer reads, so <c>verify</c> knows a table under one is meant and can check its shape. A domain exists exactly when a step reads it.</summary>
    IReadOnlyList<IImportSettingsDomain> SettingsDomains => [];
}

public static class AssetImporters
{
    /// <summary>Lowest precedence first: the chain is walked backwards so an appended importer shadows the built-in it replaces.</summary>
    public static IReadOnlyList<IAssetImporter> All { get; } =
    [
        new ConfigImporter(),
        new MaterialImporter(),
        new PrefabImporter(),
        new AudioImporter(),
        new MeshImporter(),
        new TextureImporter(),
    ];
}

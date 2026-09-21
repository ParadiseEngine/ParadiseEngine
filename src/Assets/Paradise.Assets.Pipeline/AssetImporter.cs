using Microsoft.Extensions.Logging;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Inputs, outputs and services available to an importer.</summary>
/// <remarks>
/// <paramref name="FileSystem"/> records reads and existence checks; sources are read-only,
/// case-exact and cannot be listed. The runner records other dependencies, such as tool versions
/// and profile settings, in the index environment. Verify ensures <paramref name="Meta"/> exists.
/// <paramref name="Sources"/> resolves reference GUIDs; <paramref name="Importers"/> determines
/// referenced assets' output paths through <see cref="BuiltPath"/>.
/// </remarks>
public sealed record ImportContext(
    IFileSystem FileSystem,
    AssetIndex Sources,
    AssetProjectLayout Layout,
    IReadOnlyList<IAssetImporter> Importers,
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
    /// Where the build writes the asset <paramref name="reference"/> names, assets-relative, so a
    /// built document can spell it and the runtime opens a path without knowing how the build
    /// renames things; null with <paramref name="problem"/> set when nothing will be built for it.
    /// The answer is the referenced asset's IMPORTER's, through its sidecar: the importer that
    /// writes a texture as KTX2 is the one that knows it does.
    /// </summary>
    /// <remarks>
    /// Reads through the observed filesystem so the answers are recorded dependencies: a GLB
    /// whose extraction changes rebuilds the prefabs that name it, the same way a renamed texture
    /// does.
    /// </remarks>
    public string? BuiltPath(Paradise.Authoring.AssetReference reference, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(reference);

        problem = null;
        var resolution = Resolve(reference);
        if (!resolution.Found)
        {
            problem = $"references '{reference.Path}' (guid {DocumentGuid.Format(reference.Guid)}), which no asset under assets/ carries";
            return null;
        }

        var meta = AssetSidecar.TryLoad(FileSystem, resolution.Asset, Importers);
        var chain = ImporterChain.For(Importers, new ImportCandidate(FileSystem, Layout, resolution.Asset, meta));
        // Verify refuses such a sidecar before a build reaches here; this is for a host that bakes
        // without verifying first, and it says the same thing verify would.
        if (chain.Unknown)
        {
            problem = $"references '{resolution.Path}', whose sidecar names importer '{chain.Name}', which this chain does not have (it has: {ImporterChain.Resolution.Known(Importers)}), so nothing will exist for it in the build tree";
            return null;
        }

        if (chain.Importer is not { } importer)
        {
            problem = $"references '{resolution.Path}', which no importer in this chain claims, so nothing will exist for it in the build tree";
            return null;
        }

        return importer.BuiltPath(this, resolution, out problem);
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

    /// <summary>
    /// Where the build writes <paramref name="asset"/>, assets-relative, for a document that
    /// references it: the path this importer's <see cref="Import"/> writes for it. The default is
    /// the asset's own path, which is right for every importer that writes an asset at its own
    /// name (mesh, animation, audio); one that renames (a texture to KTX2, a document to the
    /// profile's extension) or builds something else in its place (a GLB, whose mesh document is
    /// what ships) says so here. Null with <paramref name="problem"/> set when nothing will be built.
    /// </summary>
    string? BuiltPath(ImportContext context, ReferenceResolution asset, out string? problem)
    {
        problem = null;
        return asset.Path;
    }

    /// <summary>The sidecar settings domains this importer reads, so <c>verify</c> knows a table under one is meant and can check its shape. A domain exists exactly when a step reads it.</summary>
    IReadOnlyList<IImportSettingsDomain> SettingsDomains => [];

    // Extraction: what a SOURCE CONTAINER turns into
    //
    // A GLB is a container, and a game's own format is another. Extraction is the same importer's
    // other half rather than a second chain, so one Claims decides both and the sidecar's recorded
    // `importer` name dispatches both — hand-edit that line and extraction follows it too.
    //
    // It is NOT called from Import, and must never be: these WRITE into assets/ and mint
    // identities, while ImportContext.FileSystem is read-only there because the build index
    // records every read to decide what to rebuild. A build that wrote its own inputs would dirty
    // the tree on every CI run and invalidate its own index mid-run. `extract` and `watch` call
    // them; `build` never does.

    /// <summary>The kinds this importer's extraction writes, which is also what says whether it extracts at all. Empty for the many importers that only build a file someone else authored.</summary>
    IReadOnlyList<ExtractKindDeclaration> ExtractKinds => [];

    /// <summary>Whether this importer reads its asset as a source container. Derived, so there is no second thing to keep in step.</summary>
    bool Extracts => ExtractKinds.Count > 0;

    /// <summary>Whether the container holds anything to write documents for at all — an empty one is not an error, just nothing to do.</summary>
    bool HasParts(IFileSystem fileSystem, UPath source) => false;

    /// <summary>Whether it holds anything only <c>extract</c> writes (materials, embedded images); the documents the watcher mints on its own are not that.</summary>
    bool HasAuthoredParts(IFileSystem fileSystem, UPath source) => false;

    /// <summary>Whether <c>extract</c> has already run for it, as the sidecar records.</summary>
    bool IsExtracted(IFileSystem fileSystem, UPath source) => false;

    /// <summary>The full verb: every part, including the ones an author owns from the moment they exist.</summary>
    ExtractResult Extract(ExtractRequest request) => ExtractResult.NothingToExtract;

    /// <summary>
    /// Only the parts that carry no author work, for the watcher: minting those on a save is the
    /// same class of action as minting a sidecar, while writing a material or a prefab under an
    /// author is not.
    /// </summary>
    ExtractResult MintReferences(ExtractRequest request) => ExtractResult.NothingToExtract;
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
        new GlbImporter(),
        new MeshImporter(),
        new SkinnedMeshImporter(),
        new AnimationImporter(),
        new TextureImporter(),
    ];
}

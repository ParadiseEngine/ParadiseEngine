using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>Everything an importer may draw on: the runner's toolbox, plus the one asset at hand.</summary>
/// <param name="FileSystem">The filesystem holding the project — the SOURCE side, read from.</param>
/// <param name="AssetsRoot">The <c>assets/</c> tree root, for importers that resolve references to other assets.</param>
/// <param name="Asset">The source asset's absolute path.</param>
/// <param name="Source">The same path relative to <c>assets/</c> — the name errors and manifest entries use.</param>
/// <param name="Meta">
/// The asset's sidecar — <see langword="null"/> exactly when the asset IS one, because a sidecar
/// has no sidecar of its own. Every other asset has one; verify refused the build otherwise.
/// </param>
/// <param name="Profile">The build profile being compiled.</param>
/// <param name="Target">
/// Which tree is being built. An importer that exists for one target only decides that HERE, in
/// its own <see cref="IAssetImporter.Import"/>, by declining — see <see cref="SidecarImporter"/>.
/// </param>
/// <param name="Output">
/// The tree being built, mounted at its root: <c>/</c> here IS the output directory, so an
/// importer cannot write outside it — the mount is the capability, not a convention. Writes are
/// observed and become the build's manifest entries, and a write's parent directories are
/// created on demand.
/// </param>
/// <param name="Cache">The content-addressed artifact cache.</param>
/// <param name="Encoder">The texture encoder, or <see langword="null"/> when no <c>ktx</c> is available.</param>
/// <param name="Log">Progress lines, when anyone is listening.</param>
public sealed record ImportContext(
    IFileSystem FileSystem,
    UPath AssetsRoot,
    UPath Asset,
    string Source,
    SidecarMeta? Meta,
    BuildProfile Profile,
    ProjectOutputTarget Target,
    IFileSystem Output,
    ArtifactCache Cache,
    ITextureEncoder? Encoder,
    Action<string>? Log)
{
    /// <summary>Whether <see cref="Asset"/> carries one of <paramref name="extensions"/> (with dot, case-insensitive).</summary>
    /// <remarks>
    /// The first line of nearly every importer, because the extension is nearly always the first
    /// half of "is this mine". The other half — target, profile, where the file sits — is the
    /// importer's own business, and it is spelled out beside this call rather than declared.
    /// </remarks>
    public bool HasExtension(IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        var extension = Asset.GetExtensionWithDot() ?? string.Empty;
        foreach (var claimed in extensions)
        {
            if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Whether this asset is the project manifest, which configures the build rather than being built by it.</summary>
    public bool IsManifest => Source == AssetProjectLayout.ManifestFileName;
}

/// <summary>
/// One link in the import chain: given an asset, either handle it — turning it into the file(s)
/// it writes to <see cref="ImportContext.Output"/> — or decline, and let the next link try.
/// </summary>
/// <remarks>
/// <para>
/// <b>The runner does not choose an importer; the importers choose.</b> Every asset is offered to
/// the whole chain, LAST link first, until one answers <see langword="true"/>. There is no lookup
/// table, so an importer's claim is not a static fact the runner has to be taught — it is
/// whatever its own <see cref="Import"/> decides, on the extension, the target, the profile, the
/// asset's place in the tree, or its bytes. That is what lets a project append an importer that
/// shadows a built-in: appended means later means asked first.
/// </para>
/// <para>
/// Importers write <see cref="ImportContext.Output"/> directly; the runner records what was
/// actually written, so there is no reported file list to drift from reality. The discipline
/// this asks of an importer: <b>decline first, validate next, write last</b>. Declining after a
/// write would hand the next link a tree it did not make — the chain shares one output mount, so
/// such a write lands in the manifest under whoever ends up handling the asset — and an error
/// reported through <c>errors</c> after a write leaves that file in a tree the failed build has
/// already declared suspect (the index is not saved, and <c>clean</c> is the remedy). All
/// current importers decline on their first line and write on their last.
/// </para>
/// </remarks>
public interface IAssetImporter
{
    /// <summary>A short name for logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>The extensions (with dot) this importer can be interested in. Case-insensitive.</summary>
    /// <remarks>
    /// <b>Declaration, not dispatch.</b> Nothing routes on this — <see cref="Import"/> is the
    /// only thing that decides. It exists so <c>verify</c>, which builds nothing and can
    /// therefore run no chain, can still tell a texture it recognises from a stray
    /// <c>notes.txt</c> (<see cref="AssetImporters.Recognises"/>). An importer may list an
    /// extension it then declines — <see cref="SidecarImporter"/> does, outside the play target
    /// — but it must not handle one it never listed, or verify will warn about a file the build
    /// goes on to compile.
    /// </remarks>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Whether output is a pure function of the source and sidecar bytes, letting
    /// <see cref="BuildIndex"/> reuse it. A step whose output also depends on tool versions,
    /// profile flags or referenced files answers <see langword="false"/> and does its own
    /// caching keyed on the complete input.
    /// </summary>
    bool DeterministicCopy { get; }

    /// <summary>
    /// Whether the files this importer writes are recorded under the source asset's identity.
    /// False for outputs that DESCRIBE identity rather than having one (a copied sidecar), or
    /// that are addressed by path alone (a config).
    /// </summary>
    bool RecordsIdentity { get; }

    /// <summary>
    /// Handles one asset, writing its output file(s) to <see cref="ImportContext.Output"/>.
    /// A failure is reported through <paramref name="errors"/> — named for the author, prefixed
    /// with <see cref="ImportContext.Source"/> — and writes nothing.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this importer handled the asset: the chain stops here, and
    /// this importer's <see cref="DeterministicCopy"/> and <see cref="RecordsIdentity"/> answer
    /// for the writes. <see langword="false"/> to pass the asset to the next link, having
    /// written nothing and reported nothing. A handled asset that FAILED still returns
    /// <see langword="true"/> — the failure is this importer's, and offering the asset onward
    /// would let a second one quietly build it anyway.
    /// </returns>
    bool Import(ImportContext context, List<string> errors);
}

/// <summary>The import chain: every step there is, in precedence order.</summary>
public static class AssetImporters
{
    /// <summary>
    /// Every import step, <b>lowest precedence first</b> — the chain is walked backwards, so a
    /// later row is offered an asset before an earlier one, and an appended importer shadows the
    /// built-in it replaces.
    /// </summary>
    /// <remarks>
    /// There are no per-target sets any more. One chain serves every target, and an importer
    /// that belongs to one of them declines in the others (<see cref="ImportContext.Target"/>) —
    /// so "which tree is this" is answered where the answer matters, instead of by the caller
    /// assembling a list per flavour.
    /// </remarks>
    public static IReadOnlyList<IAssetImporter> All { get; } =
    [
        new SidecarImporter(),
        new ConfigImporter(),
        new PrefabImporter(),
        new AudioImporter(),
        new MeshImporter(),
        new TextureImporter(),
    ];

    /// <summary>
    /// Whether any importer lists <paramref name="path"/>'s extension — "is this a kind of file
    /// the pipeline knows", which is <c>verify</c>'s question and not the build's.
    /// </summary>
    /// <remarks>
    /// Answered from <see cref="IAssetImporter.Extensions"/> rather than by running the chain,
    /// because verify has no build to run one in. It is therefore the target-independent
    /// answer: a sidecar is recognised whether or not the build being planned would carry it.
    /// </remarks>
    public static bool Recognises(UPath path)
    {
        var extension = path.GetExtensionWithDot() ?? string.Empty;
        foreach (var importer in All)
        {
            foreach (var claimed in importer.Extensions)
            {
                if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}

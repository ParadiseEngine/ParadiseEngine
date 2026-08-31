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
    IFileSystem Output,
    ArtifactCache Cache,
    ITextureEncoder? Encoder,
    Action<string>? Log);

/// <summary>
/// One import step: claims extensions, turns a source asset into the file(s) it writes to
/// <see cref="ImportContext.Output"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole contract the runner sees. It does not know what an asset IS — it finds the
/// importer that claims the extension in the target's set (<see cref="AssetImporters.For"/>) and
/// hands it the <see cref="ImportContext"/>. Everything kind-specific — settings domains,
/// encoders, output formats — is internal to an importer, so a pipeline flavor is a CHOICE OF
/// SET: the same asset meets a different importer and becomes different files.
/// </para>
/// <para>
/// Importers write <see cref="ImportContext.Output"/> directly; the runner records what was
/// actually written, so there is no reported file list to drift from reality. The discipline
/// this asks of an importer: <b>validate first, write last</b> — an error reported through
/// <c>errors</c> after a write leaves that file in a tree the failed build has already declared
/// suspect (the index is not saved, and <c>clean</c> is the remedy), but there is no reason to
/// create that situation when checking first is possible. All current importers do.
/// </para>
/// </remarks>
public interface IAssetImporter
{
    /// <summary>A short name for logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>The extensions (with dot) this importer claims. Case-insensitive.</summary>
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
    /// Processes one asset, writing its output file(s) to <see cref="ImportContext.Output"/>.
    /// A failure is reported through <paramref name="errors"/> — named for the author, prefixed
    /// with <see cref="ImportContext.Source"/> — and writes nothing.
    /// </summary>
    void Import(ImportContext context, List<string> errors);
}

/// <summary>The import steps, the per-target sets of them, and which one claims a path.</summary>
public static class AssetImporters
{
    private static readonly TextureImporter s_texture = new();
    private static readonly MeshImporter s_mesh = new();
    private static readonly AudioImporter s_audio = new();
    private static readonly PrefabImporter s_prefab = new();
    private static readonly ConfigImporter s_config = new();
    private static readonly SidecarImporter s_sidecar = new();

    /// <summary>
    /// Every import step any target uses — the union the classifier and verify reason over.
    /// Adding an asset type is adding a row here and to the sets that build it.
    /// </summary>
    public static IReadOnlyList<IAssetImporter> All { get; } =
        [s_texture, s_mesh, s_audio, s_prefab, s_config, s_sidecar];

    /// <summary>
    /// The set of importers a target builds with. A pipeline flavor is exactly a set: the PLAY
    /// tree carries sidecars (the editor traces built assets back to their authoring identity)
    /// and a player's install does not, so the Build set simply lacks that importer — no
    /// importer ever branches on the target.
    /// </summary>
    public static IReadOnlyList<IAssetImporter> For(ProjectOutputTarget target) =>
        target == ProjectOutputTarget.Play
            ? [s_texture, s_mesh, s_audio, s_prefab, s_config, s_sidecar]
            : [s_texture, s_mesh, s_audio, s_prefab, s_config];

    /// <summary>The importer in <paramref name="importers"/> claiming <paramref name="path"/>'s extension, or <see langword="null"/>.</summary>
    public static IAssetImporter? Find(IReadOnlyList<IAssetImporter> importers, UPath path)
    {
        var extension = path.GetExtensionWithDot() ?? string.Empty;
        foreach (var importer in importers)
        {
            foreach (var claimed in importer.Extensions)
            {
                if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase)) return importer;
            }
        }

        return null;
    }

    /// <summary>The importer ANY target would route <paramref name="path"/> to, or <see langword="null"/> — the classifier's question.</summary>
    public static IAssetImporter? Find(UPath path) => Find(All, path);
}

using Zio;

namespace Paradise.Assets.Project;

/// <summary>
/// The directory layout of one asset project, rooted at the directory that holds
/// <c>assets/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three trees, and the split between them is the whole design. <c>assets/</c> is the committed
/// source of truth, read by tooling only. <c>.editor/</c> and <c>build/</c> are gitignored and
/// derived: <b>deleting either loses nothing</b>, because both are pure functions of
/// <c>assets/</c> plus the tool versions. Anything that would break that invariant — a setting
/// that lives only in <c>.editor/</c>, an artifact nothing can regenerate — belongs in
/// <c>assets/</c> instead.
/// </para>
/// <para>
/// Paths are <see cref="UPath"/> throughout, so a layout is meaningful against any
/// <see cref="Zio.IFileSystem"/> — a physical checkout in production, a
/// <see cref="Zio.FileSystems.MemoryFileSystem"/> in tests. The type is pure addressing and
/// touches no filesystem except in <see cref="TryLocate"/>.
/// </para>
/// </remarks>
public sealed class AssetProjectLayout
{
    /// <summary>Committed source of truth. Read by tooling; never by the runtime.</summary>
    public const string AssetsDirectoryName = "assets";

    /// <summary>Gitignored editor cache. Absorbs the addon's former <c>.paradise-cache</c>.</summary>
    public const string EditorDirectoryName = ".editor";

    /// <summary>Gitignored final output, produced by the build CLI and by CI.</summary>
    public const string BuildDirectoryName = "build";

    /// <summary>The project manifest, inside <c>assets/</c> because it is authored, not derived.</summary>
    public const string ManifestFileName = "project.toml";

    /// <summary>
    /// Creates a layout for the project rooted at <paramref name="root"/>. No filesystem access:
    /// the root is taken on trust, since callers that need the check have
    /// <see cref="TryLocate"/>.
    /// </summary>
    /// <param name="root">The directory containing <c>assets/</c>.</param>
    public AssetProjectLayout(UPath root)
    {
        root.AssertNotNull(nameof(root));
        Root = root.ToAbsolute();
    }

    /// <summary>The project root — the parent of <c>assets/</c>, <c>.editor/</c> and <c>build/</c>.</summary>
    public UPath Root { get; }

    /// <summary>The committed source tree, <c>&lt;root&gt;/assets</c>.</summary>
    public UPath Assets => Root / AssetsDirectoryName;

    /// <summary>The project manifest, <c>&lt;root&gt;/assets/project.toml</c>.</summary>
    public UPath Manifest => Assets / ManifestFileName;

    /// <summary>The editor cache root, <c>&lt;root&gt;/.editor</c>.</summary>
    public UPath Editor => Root / EditorDirectoryName;

    /// <summary>Materialized working <c>.blend</c> files — derived from scene documents, disposable.</summary>
    public UPath EditorBlend => Editor / "blend";

    /// <summary>
    /// Content-addressed artifacts, <c>&lt;root&gt;/.editor/cache</c>. Shared byte-for-byte with
    /// the Blender addon, which is why <see cref="ArtifactCache"/> mirrors its digest scheme
    /// rather than inventing one.
    /// </summary>
    public UPath EditorCache => Editor / "cache";

    /// <summary>
    /// Build-shaped dev output, <c>&lt;root&gt;/.editor/play</c>. Layout-identical to
    /// <see cref="Build"/> on purpose: playmode runs the same loader over the same shape, so a
    /// playmode bug is a build bug.
    /// </summary>
    public UPath EditorPlay => Editor / "play";

    /// <summary>Editor bookkeeping — stamps and caches, <c>&lt;root&gt;/.editor/state.toml</c>.</summary>
    public UPath EditorState => Editor / "state.toml";

    /// <summary>The final output tree, <c>&lt;root&gt;/build</c>.</summary>
    public UPath Build => Root / BuildDirectoryName;

    /// <summary>
    /// The output tree a build profile writes to.
    /// </summary>
    /// <param name="target">Which of the two build-shaped trees is wanted.</param>
    public UPath OutputFor(ProjectOutputTarget target) => target switch
    {
        ProjectOutputTarget.Build => Build,
        ProjectOutputTarget.Play => EditorPlay,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown output target."),
    };

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for the first directory holding
    /// <c>assets/project.toml</c>.
    /// </summary>
    /// <remarks>
    /// The manifest FILE is the marker rather than the <c>assets/</c> directory: a game repo can
    /// easily contain some other <c>assets</c> folder, and locating a project on that would put
    /// the whole build in the wrong place with no error to point at.
    /// </remarks>
    /// <param name="fileSystem">The filesystem to search.</param>
    /// <param name="startDirectory">Where to start; typically the process working directory.</param>
    /// <param name="layout">The located layout, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a project was found at or above the start directory.</returns>
    public static bool TryLocate(IFileSystem fileSystem, UPath startDirectory, out AssetProjectLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        startDirectory.AssertNotNull(nameof(startDirectory));

        var current = startDirectory.ToAbsolute();
        while (!current.IsNull)
        {
            if (fileSystem.FileExists(current / AssetsDirectoryName / ManifestFileName))
            {
                layout = new AssetProjectLayout(current);
                return true;
            }

            var parent = current.GetDirectory();
            if (parent == current) break;
            current = parent;
        }

        layout = null;
        return false;
    }

    /// <summary>
    /// <see cref="TryLocate"/>, throwing when there is no project above
    /// <paramref name="startDirectory"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">No <c>assets/project.toml</c> was found.</exception>
    public static AssetProjectLayout Locate(IFileSystem fileSystem, UPath startDirectory)
    {
        if (TryLocate(fileSystem, startDirectory, out var layout)) return layout!;
        throw new DirectoryNotFoundException(
            $"No Paradise asset project at or above '{startDirectory}': expected an " +
            $"'{AssetsDirectoryName}/{ManifestFileName}' in some parent directory.");
    }
}

/// <summary>
/// Which build-shaped output tree a caller wants. Both have the same layout; they differ only in
/// who writes them and whether they ship.
/// </summary>
public enum ProjectOutputTarget
{
    /// <summary>The shipped tree, <c>build/</c>.</summary>
    Build,

    /// <summary>The editor's incremental dev tree, <c>.editor/play/</c>.</summary>
    Play,
}

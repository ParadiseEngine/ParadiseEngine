using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Project;

/// <summary>
/// Composes the standard filesystem view of a located project.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin — composition and nothing else. The value it adds is that every tool agrees
/// on the same three mount names, so a path in a log or a manifest means the same thing wherever
/// it came from.
/// </para>
/// <para>
/// <c>/assets</c> is mounted <b>read-only</b>. That is a real guard rather than documentation:
/// the pipeline is the only component allowed to write sources, and it does so through the
/// underlying filesystem. Anything reaching for this mount is a consumer, and a consumer that
/// writes to <c>assets/</c> has just made the build tree unreproducible.
/// </para>
/// </remarks>
public static class ProjectMounts
{
    /// <summary>Mount name of the committed source tree.</summary>
    public const string AssetsMountName = "/assets";

    /// <summary>Mount name of the content-addressed artifact cache.</summary>
    public const string CacheMountName = "/cache";

    /// <summary>Mount name of the shipped output tree.</summary>
    public const string BuildMountName = "/build";

    /// <summary>Mount name of the editor's dev output tree.</summary>
    public const string PlayMountName = "/play";

    /// <summary>The mount name an output target is published under.</summary>
    /// <param name="target">Which output tree.</param>
    public static UPath MountNameFor(ProjectOutputTarget target) => target switch
    {
        ProjectOutputTarget.Build => BuildMountName,
        ProjectOutputTarget.Play => PlayMountName,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown output target."),
    };

    /// <summary>
    /// Mounts <c>/assets</c> (read-only), <c>/cache</c> and the chosen output tree.
    /// </summary>
    /// <remarks>
    /// The cache and output directories are created if absent, because both are derived and a
    /// caller asking for them is about to fill them. <c>assets/</c> is not: a missing source tree
    /// is a mislocated project, and creating an empty one would turn that into a build that
    /// succeeds and produces nothing.
    /// </remarks>
    /// <param name="fileSystem">The filesystem holding the project. Not disposed with the result.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="output">Which output tree to mount.</param>
    /// <returns>
    /// A mount filesystem owning the views it composes — disposing it releases them, but leaves
    /// <paramref name="fileSystem"/> alone.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">The project has no <c>assets/</c> directory.</exception>
    public static MountFileSystem Create(IFileSystem fileSystem, AssetProjectLayout layout, ProjectOutputTarget output)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var mounts = new MountFileSystem(owned: true);
        try
        {
            // owned: false on the SubFileSystem — the caller's filesystem outlives these mounts.
            var assets = new SubFileSystem(fileSystem, layout.Assets, owned: false);
            mounts.Mount(AssetsMountName, new ReadOnlyFileSystem(assets, owned: true));
            mounts.Mount(CacheMountName, OpenOrCreate(fileSystem, layout.EditorCache));
            mounts.Mount(MountNameFor(output), OpenOrCreate(fileSystem, layout.OutputFor(output)));
            return mounts;
        }
        catch
        {
            mounts.Dispose();
            throw;
        }
    }

    private static SubFileSystem OpenOrCreate(IFileSystem fileSystem, UPath directory)
    {
        if (!fileSystem.DirectoryExists(directory)) fileSystem.CreateDirectory(directory);
        return new SubFileSystem(fileSystem, directory, owned: false);
    }
}

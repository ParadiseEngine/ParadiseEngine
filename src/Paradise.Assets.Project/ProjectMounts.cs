using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Project;

/// <summary>The standard mount names every tool agrees on. <c>/assets</c> is read-only as a guard: a consumer that writes sources has made the build tree unreproducible.</summary>
public static class ProjectMounts
{
    public const string AssetsMountName = "/assets";

    public const string CacheMountName = "/cache";

    public const string BuildMountName = "/build";

    public const string PlayMountName = "/play";

    public static UPath MountNameFor(ProjectOutputTarget target) => target switch
    {
        ProjectOutputTarget.Build => BuildMountName,
        ProjectOutputTarget.Play => PlayMountName,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown output target."),
    };

    /// <summary>Derived directories are created on demand; <c>assets/</c> is not, because creating an empty one turns a mislocated project into a build that succeeds and produces nothing.</summary>
    /// <exception cref="DirectoryNotFoundException">The project has no <c>assets/</c> directory.</exception>
    public static MountFileSystem Create(IFileSystem fileSystem, AssetProjectLayout layout, ProjectOutputTarget output)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var mounts = new MountFileSystem(owned: true);
        try
        {
            // owned: false — the caller's filesystem outlives these mounts.
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

namespace Paradise.Assets.Project.Test;

public class ProjectMountsTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task the_build_view_mounts_assets_cache_and_build()
    {
        using var fileSystem = CreateProject();
        using var mounts = ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Build);

        await Assert.That(mounts.IsMounted(ProjectMounts.AssetsMountName)).IsTrue();
        await Assert.That(mounts.IsMounted(ProjectMounts.CacheMountName)).IsTrue();
        await Assert.That(mounts.IsMounted(ProjectMounts.BuildMountName)).IsTrue();
        await Assert.That(mounts.IsMounted(ProjectMounts.PlayMountName)).IsFalse();
    }

    [Test]
    public async Task the_play_view_mounts_the_editor_tree_under_the_same_shape()
    {
        using var fileSystem = CreateProject();
        using var mounts = ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Play);

        await Assert.That(mounts.IsMounted(ProjectMounts.PlayMountName)).IsTrue();
        await Assert.That(mounts.IsMounted(ProjectMounts.BuildMountName)).IsFalse();

        mounts.CreateDirectory("/play/scenes");
        mounts.WriteAllText("/play/scenes/district.toml", "ok");
        await Assert.That(fileSystem.ReadAllText("/game/.editor/play/scenes/district.toml")).IsEqualTo("ok");
    }

    [Test]
    public async Task sources_are_readable_through_the_assets_mount()
    {
        using var fileSystem = CreateProject();
        fileSystem.CreateDirectory("/game/assets/scenes");
        fileSystem.WriteAllText("/game/assets/scenes/district.scene", "authored");

        using var mounts = ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Build);

        await Assert.That(mounts.ReadAllText("/assets/scenes/district.scene")).IsEqualTo("authored");
    }

    [Test]
    public async Task the_assets_mount_refuses_writes()
    {
        // Not documentation — a guard. Only the pipeline writes sources, and it does so through
        // the underlying filesystem; anything writing here has just made the build tree
        // unreproducible.
        using var fileSystem = CreateProject();
        using var mounts = ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Build);

        await Assert.That(() => mounts.WriteAllText("/assets/sneaky.toml", "no"))
            .Throws<System.IO.IOException>();
    }

    [Test]
    public async Task the_derived_directories_are_created_on_demand()
    {
        // Both are pure functions of assets/, so a caller asking for them is about to fill them.
        using var fileSystem = CreateProject();
        await Assert.That(fileSystem.DirectoryExists("/game/.editor/cache")).IsFalse();

        using var mounts = ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Build);

        await Assert.That(fileSystem.DirectoryExists("/game/.editor/cache")).IsTrue();
        await Assert.That(fileSystem.DirectoryExists("/game/build")).IsTrue();
    }

    [Test]
    public async Task a_missing_assets_tree_is_refused_rather_than_created()
    {
        // Creating an empty assets/ would turn "the project is somewhere else" into a build that
        // succeeds and produces nothing.
        using var fileSystem = new MemoryFileSystem();

        await Assert.That(() => ProjectMounts.Create(fileSystem, s_layout, ProjectOutputTarget.Build))
            .Throws<System.IO.DirectoryNotFoundException>();
    }

    private static MemoryFileSystem CreateProject()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory(s_layout.Assets);
        fileSystem.WriteAllText(s_layout.Manifest, "name = \"shiningpie\"\nschema_version = 1\n");
        return fileSystem;
    }
}

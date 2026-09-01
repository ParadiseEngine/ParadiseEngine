namespace Paradise.Assets.Project.Test;

public class AssetProjectLayoutTests
{
    [Test]
    public async Task the_three_trees_hang_off_the_root()
    {
        var layout = new AssetProjectLayout("/game");

        await Assert.That(layout.Root).IsEqualTo(new UPath("/game"));
        await Assert.That(layout.Assets).IsEqualTo(new UPath("/game/assets"));
        await Assert.That(layout.Manifest).IsEqualTo(new UPath("/game/assets/project.toml"));
        await Assert.That(layout.Editor).IsEqualTo(new UPath("/game/.editor"));
        await Assert.That(layout.EditorBlend).IsEqualTo(new UPath("/game/.editor/blend"));
        await Assert.That(layout.EditorCache).IsEqualTo(new UPath("/game/.editor/cache"));
        await Assert.That(layout.EditorPlay).IsEqualTo(new UPath("/game/.editor/play"));
        await Assert.That(layout.EditorState).IsEqualTo(new UPath("/game/.editor/state.toml"));
        await Assert.That(layout.Build).IsEqualTo(new UPath("/game/build"));
    }

    [Test]
    public async Task the_output_trees_are_build_and_editor_play()
    {
        var layout = new AssetProjectLayout("/game");

        await Assert.That(layout.OutputFor(ProjectOutputTarget.Build)).IsEqualTo(layout.Build);
        await Assert.That(layout.OutputFor(ProjectOutputTarget.Play)).IsEqualTo(layout.EditorPlay);
    }

    [Test]
    public async Task locate_walks_up_to_the_manifest()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/work/game/assets/models/props");
        fileSystem.WriteAllText("/work/game/assets/project.toml", "name = \"x\"\nschema_version = 1");

        var layout = AssetProjectLayout.Locate(fileSystem, "/work/game/assets/models/props");

        await Assert.That(layout.Root).IsEqualTo(new UPath("/work/game"));
    }

    [Test]
    public async Task locate_finds_a_project_at_the_start_directory_itself()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets");
        fileSystem.WriteAllText("/game/assets/project.toml", "");

        await Assert.That(AssetProjectLayout.TryLocate(fileSystem, "/game", out var layout)).IsTrue();
        await Assert.That(layout!.Root).IsEqualTo(new UPath("/game"));
    }

    [Test]
    public async Task an_assets_directory_without_a_manifest_is_not_a_project()
    {
        // A game repo can easily hold some other "assets" folder; locating on the directory
        // rather than the manifest would silently root the build in the wrong place.
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/work/notagame/assets");

        await Assert.That(AssetProjectLayout.TryLocate(fileSystem, "/work/notagame/assets", out var layout)).IsFalse();
        await Assert.That(layout).IsNull();
    }

    [Test]
    public async Task locate_gives_up_at_the_filesystem_root_rather_than_looping()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/somewhere/deep");

        await Assert.That(AssetProjectLayout.TryLocate(fileSystem, "/somewhere/deep", out _)).IsFalse();
        await Assert.That(AssetProjectLayout.TryLocate(fileSystem, "/", out _)).IsFalse();
    }

    [Test]
    public async Task locate_says_what_it_looked_for_when_there_is_no_project()
    {
        using var fileSystem = new MemoryFileSystem();

        await Assert.That(() => AssetProjectLayout.Locate(fileSystem, "/nowhere"))
            .Throws<System.IO.DirectoryNotFoundException>();
    }

    [Test]
    public async Task the_nearest_project_wins_over_an_outer_one()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/outer/assets");
        fileSystem.WriteAllText("/outer/assets/project.toml", "");
        fileSystem.CreateDirectory("/outer/inner/assets/scenes");
        fileSystem.WriteAllText("/outer/inner/assets/project.toml", "");

        var layout = AssetProjectLayout.Locate(fileSystem, "/outer/inner/assets/scenes");

        await Assert.That(layout.Root).IsEqualTo(new UPath("/outer/inner"));
    }
}

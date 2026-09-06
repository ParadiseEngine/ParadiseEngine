using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class HostPlayArgumentsTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_document_under_assets_plays_from_its_play_tree_twin()
    {
        var scene = HostPlayArguments.ResolveScene(s_layout, "/game/assets/levels/arena.prefab");

        await Assert.That(scene).IsEqualTo((UPath)"/game/.editor/play/levels/arena.prefab");
    }

    [Test]
    public async Task a_path_outside_assets_passes_through()
    {
        var scene = HostPlayArguments.ResolveScene(s_layout, "/game/build/levels/arena.json");

        await Assert.That(scene).IsEqualTo((UPath)"/game/build/levels/arena.json");
    }

    [Test]
    public async Task the_manifests_scene_is_the_default_and_the_callers_wins()
    {
        var host = new HostSettings("G/G.csproj", [], "levels/arena.prefab");

        await Assert.That(HostPlayArguments.ChooseScene(s_layout, null, host)).IsEqualTo((UPath)"/game/assets/levels/arena.prefab");
        await Assert.That(HostPlayArguments.ChooseScene(s_layout, "/game/assets/levels/other.prefab", host)).IsEqualTo((UPath)"/game/assets/levels/other.prefab");
        await Assert.That(HostPlayArguments.ChooseScene(s_layout, null, HostSettings.None)).IsNull();
    }

    [Test]
    public async Task the_config_is_the_projects_when_the_play_tree_has_one()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/.editor/play/arena");
        fileSystem.WriteAllText("/game/.editor/play/arena/config.toml", "");

        var config = HostPlayArguments.FindConfig(fileSystem, s_layout, "arena");

        await Assert.That(config).IsEqualTo((UPath)"/game/.editor/play/arena/config.toml");
        await Assert.That(HostPlayArguments.FindConfig(fileSystem, s_layout, "other")).IsNull();
    }

    [Test]
    public async Task launcher_flags_come_first_then_the_manifests_then_the_callers()
    {
        var arguments = HostPlayArguments.Compose(
            static path => path.FullName,
            "/game/.editor/play/levels/a.prefab",
            "/game/.editor/play/g/config.toml",
            ["--ui", "ui/Shell.xaml"],
            ["--seed", "7"]);

        await Assert.That(arguments).IsEquivalentTo(new[]
        {
            "--scene", "/game/.editor/play/levels/a.prefab",
            "--config", "/game/.editor/play/g/config.toml",
            "--ui", "ui/Shell.xaml",
            "--seed", "7",
        });
    }
}

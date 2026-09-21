using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>The tray's game items exist exactly when the manifest says what to play; the click paths need a launcher and are not exercised here.</summary>
public class TrayGameSessionTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private static MemoryFileSystem Project(string host)
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/assets");
        fileSystem.WriteAllText("/game/assets/project.toml", "name = \"g\"\nschema_version = 1\n" + host);
        return fileSystem;
    }

    [Test]
    public async Task no_host_scene_means_no_game_items()
    {
        using var fileSystem = Project("[host]\nproject = \"G/G.csproj\"\n");

        var hooks = TrayGameSession.Create(fileSystem, s_layout, "dev", AssetImporters.All, out var session);

        await Assert.That(hooks).IsNull();
        await Assert.That(session).IsNull();
    }

    [Test]
    public async Task a_host_scene_gives_the_tray_its_three_items()
    {
        using var fileSystem = Project("[host]\nproject = \"G/G.csproj\"\nscene = \"levels/a.prefab\"\n");

        var hooks = TrayGameSession.Create(fileSystem, s_layout, "dev", AssetImporters.All, out var session);

        await Assert.That(hooks).IsNotNull();
        await Assert.That(session).IsNotNull();
        // Nothing runs yet, so a Stop is a no-op rather than a fault.
        hooks!.StopGame();
        await Assert.That(hooks.SceneRestart.IsOn).IsTrue();
        hooks.ToggleSceneRestart();
        await Assert.That(session!.SceneRestart.IsOn).IsFalse();
        session.Dispose();
    }

    [Test]
    public async Task an_unreadable_manifest_hides_the_items_rather_than_failing_the_watch()
    {
        using var fileSystem = Project("[host]\nnope = 1\n");

        var hooks = TrayGameSession.Create(fileSystem, s_layout, "dev", AssetImporters.All, out _);

        await Assert.That(hooks).IsNull();
    }
}

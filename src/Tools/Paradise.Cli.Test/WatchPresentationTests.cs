using Paradise.Assets.Pipeline;

namespace Paradise.Cli.Test;

public class WatchPresentationTests
{
    [Test]
    public async Task tooltip_names_the_four_states()
    {
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Alive, 0)).IsEqualTo("paradise watch — watching");
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Idle, 0)).IsEqualTo("paradise watch — idle");
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Building, 0)).IsEqualTo("paradise watch — building");
    }

    [Test]
    public async Task failed_tooltip_carries_the_error_count()
    {
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Failed, 1))
            .IsEqualTo("paradise watch — failed (1 error)");
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Failed, 3))
            .IsEqualTo("paradise watch — failed (3 errors)");
    }

    [Test]
    public async Task menu_bar_title_is_the_four_glanceable_states()
    {
        await Assert.That(WatchPresentation.MenuBarTitle(WatchStatus.Alive)).IsEqualTo("⚪");
        await Assert.That(WatchPresentation.MenuBarTitle(WatchStatus.Idle)).IsEqualTo("🟢");
        await Assert.That(WatchPresentation.MenuBarTitle(WatchStatus.Building)).IsEqualTo("🟡");
        await Assert.That(WatchPresentation.MenuBarTitle(WatchStatus.Failed)).IsEqualTo("🔴");
    }

    [Test]
    public async Task last_build_menu_is_the_count_that_would_otherwise_scroll_past()
    {
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Alive, 0)).IsEqualTo("Last build: (none yet)");
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Idle, 0)).IsEqualTo("Last build: ok");
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Failed, 1)).IsEqualTo("Last build: 1 error");
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Failed, 4)).IsEqualTo("Last build: 4 errors");
    }

    [Test]
    public async Task editor_watch_names_play_mode_rebuild()
    {
        await Assert.That(WatchPresentation.RebuildMenu(editor: false)).IsEqualTo("Rebuild now");
        await Assert.That(WatchPresentation.RebuildMenu(editor: true)).IsEqualTo("Rebuild play mode");
        await Assert.That(WatchPresentation.OpenOutputMenu(editor: false)).IsEqualTo("Open the build folder");
        await Assert.That(WatchPresentation.OpenOutputMenu(editor: true)).IsEqualTo("Open the play folder");
        await Assert.That(WatchPresentation.EditorToggleMenu).IsEqualTo("Play mode");
        await Assert.That(WatchPresentation.PlayMenu).IsEqualTo("Play the game");
        await Assert.That(WatchPresentation.PlayWatchMenu).IsEqualTo("Play the game (watch code)");
        await Assert.That(WatchPresentation.StopGameMenu).IsEqualTo("Stop the game");
        await Assert.That(WatchPresentation.SceneRestartToggleMenu).IsEqualTo("Restart the game on scene save");
    }

    [Test]
    public async Task a_running_rebuild_never_claims_it_is_finished()
    {
        var almost = new WatchProgress(0.999, BuildStage.Finish, 0, 0, null);

        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Building, 0, almost)).EndsWith(" 99%");
        await Assert.That(WatchPresentation.MenuBarTitle(WatchStatus.Building, almost)).EndsWith(" 99%");
        await Assert.That(WatchPresentation.Tooltip(WatchStatus.Building, 0, almost)).EndsWith(" 99%");
    }

    [Test]
    public async Task the_progress_line_names_the_step_and_keeps_the_end_of_a_long_path()
    {
        var path = "models/neon_city/" + new string('x', 60) + "/NeonCityMidrise.blend";
        var step = new WatchProgress(0.5, BuildStage.Assets, 119, 2279, path);

        var line = WatchPresentation.ProgressMenu(WatchStatus.Building, step)!;

        await Assert.That(line).Contains("NeonCityMidrise.blend (120/2279)");
        await Assert.That(line).DoesNotContain("models/neon_city/");
    }

    [Test]
    public async Task the_progress_line_is_hidden_unless_a_rebuild_is_reporting()
    {
        var step = new WatchProgress(0.5, BuildStage.Assets, 0, 1, "a.png");

        await Assert.That(WatchPresentation.ProgressMenu(WatchStatus.Building, null)).IsNull();
        await Assert.That(WatchPresentation.ProgressMenu(WatchStatus.Idle, step)).IsNull();
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Idle, 0, step)).IsEqualTo("Last build: ok");
    }
}

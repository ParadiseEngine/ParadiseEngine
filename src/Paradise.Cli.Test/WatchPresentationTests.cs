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
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Building, 0)).IsEqualTo("Last build: in progress");
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Failed, 1)).IsEqualTo("Last build: 1 error");
        await Assert.That(WatchPresentation.LastBuildMenu(WatchStatus.Failed, 4)).IsEqualTo("Last build: 4 errors");
    }
}

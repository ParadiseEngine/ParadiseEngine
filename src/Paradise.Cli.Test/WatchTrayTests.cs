namespace Paradise.Cli.Test;

/// <summary>
/// The additive contract: a host with no tray, or a user who asked not to have one, must get
/// the no-op — never an exception, never a different watch loop.
/// </summary>
public class WatchTrayTests
{
    private static WatchTrayHooks Hooks() => new(
        Stop: static () => { },
        Rebuild: static () => { },
        OpenOutput: static () => { });

    [Test]
    public async Task disabled_tray_is_the_no_op()
    {
        using var tray = WatchTray.Create(Hooks(), enabled: false);

        await Assert.That(tray.IsAvailable).IsFalse();
        await Assert.That(tray).IsEqualTo(NullWatchTray.Instance);
    }

    [Test]
    public async Task linux_and_macos_have_no_icon_yet_and_must_not_throw()
    {
        if (OperatingSystem.IsWindows()) return;

        using var tray = WatchTray.Create(Hooks(), enabled: true);

        await Assert.That(tray.IsAvailable).IsFalse();
        tray.SetState(WatchStatus.Failed, 2);
    }

    [Test]
    public async Task the_no_op_ignores_state_and_dispose()
    {
        NullWatchTray.Instance.SetState(WatchStatus.Building, 1);
        NullWatchTray.Instance.Dispose();
        NullWatchTray.Instance.Dispose();
        await Assert.That(NullWatchTray.Instance.IsAvailable).IsFalse();
    }
}

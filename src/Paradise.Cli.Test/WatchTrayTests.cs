namespace Paradise.Cli.Test;

/// <summary>
/// The additive contract: a host with no tray, or a user who asked not to have one, must get
/// the no-op — never an exception, never a different watch loop. Native startup (Win32 message
/// pump, AppKit run loop) is not exercised here.
/// </summary>
public class WatchTrayTests
{
    private static WatchTrayHooks Hooks() => new(
        Stop: static () => { },
        Rebuild: static () => { },
        OpenOutput: static () => { },
        Editor: new WatchEditorMode(true));

    [Test]
    public async Task disabled_tray_is_the_no_op()
    {
        using var tray = WatchTray.Create(Hooks(), enabled: false);

        await Assert.That(tray.IsAvailable).IsFalse();
        await Assert.That(tray).IsEqualTo(NullWatchTray.Instance);
    }

    [Test]
    public async Task linux_is_always_the_no_op()
    {
        if (!OperatingSystem.IsLinux()) return;

        await Assert.That(WatchTray.IsLikelyAvailable()).IsFalse();
        using var tray = WatchTray.Create(Hooks(), enabled: true);
        await Assert.That(tray.IsAvailable).IsFalse();
        await Assert.That(tray).IsEqualTo(NullWatchTray.Instance);
        tray.SetState(WatchStatus.Failed, 2);
    }

    [Test]
    public async Task ci_is_always_the_no_op()
    {
        var ci = Environment.GetEnvironmentVariable("CI");
        if (!string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ci, "1", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await Assert.That(WatchTray.IsLikelyAvailable()).IsFalse();
        using var tray = WatchTray.Create(Hooks(), enabled: true);
        await Assert.That(tray).IsEqualTo(NullWatchTray.Instance);
    }

    [Test]
    public async Task macos_create_does_not_start_appkit()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var tray = WatchTray.Create(Hooks(), enabled: true);
        if (WatchTray.IsLikelyAvailable())
        {
            await Assert.That(tray.IsAvailable).IsFalse();
            await Assert.That(tray).IsNotEqualTo(NullWatchTray.Instance);
        }
        else
        {
            await Assert.That(tray).IsEqualTo(NullWatchTray.Instance);
        }

        tray.SetState(WatchStatus.Building, 0);
    }

    [Test]
    public async Task the_no_op_ignores_state_and_dispose()
    {
        NullWatchTray.Instance.SetState(WatchStatus.Building, 1);
        NullWatchTray.Instance.Dispose();
        NullWatchTray.Instance.Dispose();
        await Assert.That(NullWatchTray.Instance.IsAvailable).IsFalse();
    }

    [Test]
    public async Task the_no_op_run_just_invokes_the_watch()
    {
        var ran = false;
        NullWatchTray.Instance.Run(() => ran = true);
        await Assert.That(ran).IsTrue();
    }
}

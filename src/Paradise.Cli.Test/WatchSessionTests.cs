using Paradise.Assets.Pipeline;

using Zio;

namespace Paradise.Cli.Test;

/// <summary>
/// The watch loop's state machine, driven without a filesystem and without a notify icon.
/// </summary>
public class WatchSessionTests
{
    private static readonly UPath s_output = "/game/build";

    private sealed class RecordingTray : IWatchTray
    {
        public List<(WatchStatus Status, int Errors)> States { get; } = [];
        public bool IsAvailable => true;
        public void SetState(WatchStatus status, int errorCount) => States.Add((status, errorCount));
        public void Dispose() { }
    }

    private static BuildResult Ok(int assets = 2) => new(true, [], assets, s_output);

    private static BuildResult Fail(params string[] errors) => new(false, errors, 0, s_output);

    private static WatchSession Session(
        WatchSignals signals,
        RecordingTray tray,
        List<string> log,
        Func<int> drain,
        Func<BuildResult>? rebuild) =>
        new(
            signals,
            tray,
            drain,
            rebuild,
            log.Add,
            log.Add,
            "/game/build",
            quiet: TimeSpan.Zero);

    [Test]
    public async Task a_rebuild_request_runs_even_when_nothing_was_drained()
    {
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var log = new List<string>();
        var session = Session(
            signals,
            tray,
            log,
            drain: static () => 0,
            rebuild: () =>
            {
                signals.RequestStop();
                return Ok(4);
            });
        signals.RequestRebuild();

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
        await Assert.That(session.LastErrorCount).IsEqualTo(0);
        await Assert.That(tray.States.ToArray()).IsEquivalentTo(new (WatchStatus, int)[]
        {
            (WatchStatus.Alive, 0),
            (WatchStatus.Building, 0),
            (WatchStatus.Idle, 0),
        });
        await Assert.That(log).Contains("watch: rebuilt 4 asset(s) into /game/build");
    }

    [Test]
    public async Task a_drained_change_rebuilds_without_a_menu_click()
    {
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var log = new List<string>();
        var session = Session(
            signals,
            tray,
            log,
            drain: static () => 1,
            rebuild: () =>
            {
                signals.RequestStop();
                return Ok(1);
            });

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
        await Assert.That(log).Contains("watch: rebuilt 1 asset(s) into /game/build");
        await Assert.That(tray.States.Select(s => s.Status).ToArray())
            .IsEquivalentTo(new[] { WatchStatus.Alive, WatchStatus.Building, WatchStatus.Idle });
    }

    [Test]
    public async Task a_failed_rebuild_stays_failed_with_the_error_count()
    {
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var log = new List<string>();
        var session = Session(
            signals,
            tray,
            log,
            drain: static () => 1,
            rebuild: () =>
            {
                signals.RequestStop();
                return Fail("missing sidecar", "bad guid");
            });

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Failed);
        await Assert.That(session.LastErrorCount).IsEqualTo(2);
        await Assert.That(log).Contains("error: missing sidecar");
        await Assert.That(log).Contains("error: bad guid");
        await Assert.That(log).Contains("watch: build FAILED with 2 error(s)");
        await Assert.That(tray.States.ToArray()).IsEquivalentTo(new (WatchStatus, int)[]
        {
            (WatchStatus.Alive, 0),
            (WatchStatus.Building, 0),
            (WatchStatus.Failed, 2),
        });
    }

    [Test]
    public async Task no_build_never_rebuilds_even_when_the_tree_changed()
    {
        using var signals = new WatchSignals();
        var session = Session(
            signals,
            new RecordingTray(),
            [],
            drain: () =>
            {
                signals.RequestStop();
                return 3;
            },
            rebuild: null);

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Alive);
        await Assert.That(session.LastErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task stop_before_run_does_not_rebuild()
    {
        var rebuilt = false;
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var session = Session(
            signals,
            tray,
            [],
            drain: static () => 1,
            rebuild: () =>
            {
                rebuilt = true;
                return Ok();
            });
        signals.RequestStop();

        session.Run();

        await Assert.That(rebuilt).IsFalse();
        await Assert.That(session.Status).IsEqualTo(WatchStatus.Alive);
        await Assert.That(tray.States.ToArray()).IsEquivalentTo(new (WatchStatus, int)[] { (WatchStatus.Alive, 0) });
    }

    [Test]
    public async Task a_later_success_clears_failed()
    {
        var n = 0;
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var session = Session(
            signals,
            tray,
            [],
            drain: static () => 1,
            rebuild: () =>
            {
                n++;
                if (n == 1) return Fail("nope");
                signals.RequestStop();
                return Ok(8);
            });

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
        await Assert.That(session.LastErrorCount).IsEqualTo(0);
        await Assert.That(tray.States.Select(s => s.Status).ToArray()).IsEquivalentTo(new[]
        {
            WatchStatus.Alive,
            WatchStatus.Building,
            WatchStatus.Failed,
            WatchStatus.Building,
            WatchStatus.Idle,
        });
        await Assert.That(tray.States[2].Errors).IsEqualTo(1);
        await Assert.That(tray.States[^1].Errors).IsEqualTo(0);
    }
}

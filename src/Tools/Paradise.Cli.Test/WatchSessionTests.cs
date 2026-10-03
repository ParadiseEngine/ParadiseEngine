using TUnit.Assertions.Enums;
using Paradise.Assets.Pipeline;

using Zio;

namespace Paradise.Cli.Test;

/// <summary>The watch loop's state machine, driven without a filesystem and without a notify icon.</summary>
public class WatchSessionTests
{
    private static readonly UPath s_output = "/game/build";

    private sealed class RecordingTray(Action<WatchProgress?>? progressPublished = null) : IWatchTray
    {
        public List<(WatchStatus Status, int Errors)> States { get; } = [];
        public List<WatchProgress?> Progress { get; } = [];
        public bool IsAvailable => true;
        public void SetState(WatchStatus status, int errorCount) => States.Add((status, errorCount));
        public void SetProgress(WatchProgress? progress)
        {
            Progress.Add(progress);
            progressPublished?.Invoke(progress);
        }
        public void Run(Action watch, Action<string>? log = null) => watch();
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
            rebuild is null ? null : (_, _) => rebuild(),
            log.Add,
            log.Add,
            static () => "/game/build",
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
        }, CollectionOrdering.Matching);
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
            .IsEquivalentTo(new[] { WatchStatus.Alive, WatchStatus.Building, WatchStatus.Idle }, CollectionOrdering.Matching);
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
        }, CollectionOrdering.Matching);
    }

    /// <summary>A rebuild that throws — a file Blender is still writing, a sidecar rewritten mid-build — is a failed build, not a dead watch (issue #203).</summary>
    [Test]
    public async Task a_rebuild_that_throws_is_a_failed_build_and_the_watch_goes_on()
    {
        var n = 0;
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
                n++;
                if (n == 1) throw new IOException("models/crate.glb is in use");
                signals.RequestStop();
                return Ok(3);
            });

        session.Run();

        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
        await Assert.That(log).Contains("error: rebuild threw IOException: models/crate.glb is in use");
        await Assert.That(log).Contains("watch: build FAILED with 1 error(s)");
        await Assert.That(tray.States.Select(s => s.Status).ToArray()).IsEquivalentTo(new[]
        {
            WatchStatus.Alive,
            WatchStatus.Building,
            WatchStatus.Failed,
            WatchStatus.Building,
            WatchStatus.Idle,
        }, CollectionOrdering.Matching);
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
        await Assert.That(tray.States.ToArray()).IsEquivalentTo(new (WatchStatus, int)[] { (WatchStatus.Alive, 0) }, CollectionOrdering.Matching);
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
        }, CollectionOrdering.Matching);
        await Assert.That(tray.States[2].Errors).IsEqualTo(1);
        await Assert.That(tray.States[^1].Errors).IsEqualTo(0);
    }

    [Test]
    public async Task toggling_play_mode_is_visible_on_the_next_rebuild()
    {
        var mode = new WatchToggle(true);
        var seen = new List<bool>();
        using var signals = new WatchSignals();
        var log = new List<string>();
        var folder = "/game/.editor/play";
        var session = new WatchSession(
            signals,
            new RecordingTray(),
            drain: static () => 1,
            rebuild: (_, _) =>
            {
                seen.Add(mode.IsOn);
                if (seen.Count == 1)
                {
                    mode.Toggle();
                    return Ok(1);
                }

                folder = "/game/build";
                signals.RequestStop();
                return Ok(1);
            },
            log: log.Add,
            error: log.Add,
            outputDisplay: () => folder,
            quiet: TimeSpan.Zero);

        session.Run();

        await Assert.That(seen.ToArray()).IsEquivalentTo(new[] { true, false }, CollectionOrdering.Matching);
        await Assert.That(log).Contains("watch: rebuilt 1 asset(s) into /game/.editor/play");
        await Assert.That(log).Contains("watch: rebuilt 1 asset(s) into /game/build");
    }

    [Test]
    public async Task a_rebuild_shows_its_progress_and_clears_it_when_done()
    {
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var session = new WatchSession(
            signals,
            tray,
            drain: static () => 1,
            rebuild: (report, _) =>
            {
                report(new BuildProgress(BuildStage.Verify, 0, 2, "models/a.blend"));
                report(new BuildProgress(BuildStage.Verify, 1, 2, "models/b.blend"));
                report(new BuildProgress(BuildStage.Assets, 0, 2, "models/a.blend"));
                signals.RequestStop();
                return Ok(2);
            },
            log: static _ => { },
            error: static _ => { },
            outputDisplay: static () => "/game/build",
            quiet: TimeSpan.Zero,
            refresh: Timeout.InfiniteTimeSpan);

        session.Run();

        // Without the refresh timer, each new stage is shown at once and nothing else is.
        var shown = tray.Progress.Take(tray.Progress.Count - 1).Select(progress => progress!.Value.Stage).ToArray();
        await Assert.That(shown).IsEquivalentTo(new[] { BuildStage.Verify, BuildStage.Assets }, CollectionOrdering.Matching);
        await Assert.That(tray.Progress[^1]).IsNull();
        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
    }

    [Test]
    public async Task a_rebuild_that_throws_still_clears_its_progress()
    {
        using var signals = new WatchSignals();
        var tray = new RecordingTray();
        var session = new WatchSession(
            signals,
            tray,
            drain: static () => 1,
            rebuild: (report, _) =>
            {
                report(new BuildProgress(BuildStage.Verify, 0, 5, "levels/a.prefab"));
                signals.RequestStop();
                throw new InvalidOperationException("boom");
            },
            log: static _ => { },
            error: static _ => { },
            outputDisplay: static () => "/game/build",
            quiet: TimeSpan.Zero);

        session.Run();

        await Assert.That(tray.Progress[^1]).IsNull();
        await Assert.That(session.Status).IsEqualTo(WatchStatus.Failed);
    }

    [Test]
    public async Task the_tray_is_refreshed_while_one_step_is_blocked()
    {
        using var signals = new WatchSignals();
        using var refreshed = new ManualResetEventSlim();
        var progressCount = 0;
        var observedRefresh = false;
        var tray = new RecordingTray(progress =>
        {
            // The stage report is immediate; two more publications require timer callbacks.
            if (progress is not null && ++progressCount >= 3) refreshed.Set();
        });
        var session = new WatchSession(
            signals,
            tray,
            drain: static () => 1,
            rebuild: (report, _) =>
            {
                report(new BuildProgress(BuildStage.Assets, 0, 1, "models/slow.blend"));
                observedRefresh = refreshed.Wait(TimeSpan.FromSeconds(10));
                signals.RequestStop();
                return Ok(1);
            },
            log: static _ => { },
            error: static _ => { },
            outputDisplay: static () => "/game/build",
            quiet: TimeSpan.Zero,
            refresh: TimeSpan.FromMilliseconds(10));

        session.Run();

        await Assert.That(observedRefresh).IsTrue();
        await Assert.That(session.Status).IsEqualTo(WatchStatus.Idle);
        var during = tray.Progress.Where(progress => progress is not null).ToList();
        await Assert.That(during.Count).IsGreaterThan(2);
        await Assert.That(during.All(progress => progress!.Value.Current == "models/slow.blend")).IsTrue();
        await Assert.That(tray.Progress[^1]).IsNull();
    }
}

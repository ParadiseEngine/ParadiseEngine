using Paradise.Assets.Pipeline;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>The estimate the tray's bar shows, on a clock the test moves.</summary>
public class RebuildProgressTests
{
    private sealed class ManualTime : TimeProvider
    {
        public long Ticks { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private static BuildProgress Step(BuildStage stage, int done = 0, int total = 0) => new(stage, done, total, null);

    private static WatchProgress Show(RebuildProgress progress, BuildProgress step)
    {
        progress.Report(step);
        return progress.Snapshot()!.Value;
    }

    /// <summary>One whole rebuild spending <paramref name="seconds"/> in each stage, in order.</summary>
    private static void RunThrough(RebuildProgress progress, ManualTime time, params double[] seconds)
    {
        progress.Begin();
        for (var stage = 0; stage < seconds.Length; stage++)
        {
            progress.Report(Step((BuildStage)stage));
            time.Advance(seconds[stage]);
        }

        progress.End();
    }

    [Test]
    public async Task the_first_rebuild_counts_steps_within_default_stage_weights()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        progress.Begin();

        var half = Show(progress, Step(BuildStage.Assets, 50, 100));

        var weights = RebuildProgress.DefaultWeights;
        var expected = (weights[0] + weights[1] + weights[2] * 0.5) / weights.Sum();
        await Assert.That(half.Fraction).IsEqualTo(expected).Within(1e-9);
    }

    [Test]
    public async Task a_finished_rebuild_teaches_the_next_one_where_the_time_goes()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        RunThrough(progress, time, 1, 1, 8, 0);
        RunThrough(progress, time, 1, 1, 8, 0);

        progress.Begin();
        progress.Report(Step(BuildStage.Sidecars));
        time.Advance(1);
        progress.Report(Step(BuildStage.Verify));
        time.Advance(1);
        // Two seconds of a stage that last took eight: a quarter of it, whatever the count says.
        progress.Report(Step(BuildStage.Assets, 0, 1000));
        time.Advance(2);
        var shown = Show(progress, Step(BuildStage.Assets, 900, 1000));

        await Assert.That(shown.Fraction).IsEqualTo(0.4).Within(1e-9);
    }

    [Test]
    public async Task a_slower_stage_than_last_time_waits_short_of_the_next_one()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        RunThrough(progress, time, 0, 0, 10, 0);
        RunThrough(progress, time, 0, 0, 10, 0);

        progress.Begin();
        progress.Report(Step(BuildStage.Assets, 0, 10));
        time.Advance(60);
        var shown = Show(progress, Step(BuildStage.Assets, 1, 10));

        await Assert.That(shown.Fraction).IsLessThan(1.0);
        await Assert.That(shown.Fraction).IsGreaterThanOrEqualTo(0.9);
    }

    [Test]
    public async Task a_rebuild_refused_at_verify_does_not_teach_the_estimate()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        RunThrough(progress, time, 1, 1, 8, 0);
        RunThrough(progress, time, 1, 1, 8, 0);
        // Refused: never reaches the asset walk or Finish.
        RunThrough(progress, time, 1, 1);

        progress.Begin();
        var shown = Show(progress, Step(BuildStage.Assets, 0, 10));

        await Assert.That(shown.Fraction).IsEqualTo(0.2).Within(1e-9);
    }

    [Test]
    public async Task the_fraction_never_moves_backwards_within_a_rebuild()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        progress.Begin();

        var ahead = Show(progress, Step(BuildStage.Assets, 90, 100));
        var behind = Show(progress, Step(BuildStage.Assets, 10, 100));

        await Assert.That(behind.Fraction).IsEqualTo(ahead.Fraction);
    }

    [Test]
    public async Task the_estimate_keeps_moving_through_one_long_step()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        RunThrough(progress, time, 0, 0, 10, 0);
        RunThrough(progress, time, 0, 0, 10, 0);

        progress.Begin();
        var start = Show(progress, Step(BuildStage.Assets, 0, 2));
        time.Advance(4);
        // No report in between: a model conversion, say, still running.
        var later = progress.Snapshot()!.Value;

        await Assert.That(later.Fraction).IsGreaterThan(start.Fraction);
        await Assert.That(later.Done).IsEqualTo(0);
    }

    [Test]
    public async Task nothing_is_shown_before_the_first_report()
    {
        var progress = new RebuildProgress(new ManualTime());
        progress.Begin();

        await Assert.That(progress.Snapshot()).IsNull();
    }

    [Test]
    public async Task a_walk_that_ended_in_errors_still_teaches_the_estimate()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        // Sidecars, verify and a walk of 8 s, then the build stops at its errors: no Finish.
        RunThrough(progress, time, 1, 1, 8);
        RunThrough(progress, time, 1, 1, 8);

        progress.Begin();
        var shown = Show(progress, Step(BuildStage.Assets, 0, 10));

        await Assert.That(shown.Fraction).IsEqualTo(0.2).Within(1e-9);
    }

    [Test]
    public async Task one_cold_rebuild_does_not_skew_the_estimate_of_incremental_ones()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        // The first rebuild of a project is usually the cold one: a 600 s walk, then an incremental 1 s one.
        RunThrough(progress, time, 2, 10, 600, 0);
        RunThrough(progress, time, 2, 10, 1, 0);

        progress.Begin();
        progress.Report(Step(BuildStage.Verify, 0, 100));
        time.Advance(5);
        var verifying = Show(progress, Step(BuildStage.Verify, 50, 100));

        // Half of verify is 7 s of a typical 13 s rebuild, not 7 s of a 612 s one.
        await Assert.That(verifying.Fraction).IsEqualTo(7.0 / 13).Within(1e-9);
    }

    [Test]
    public async Task the_next_watcher_starts_from_what_this_one_learned()
    {
        using var fileSystem = new MemoryFileSystem();
        var time = new ManualTime();
        var first = new RebuildProgress(time, fileSystem, "/game/.editor/watch-timing.txt");
        RunThrough(first, time, 1, 1, 8, 0);
        RunThrough(first, time, 1, 1, 8, 0);

        var next = new RebuildProgress(time, fileSystem, "/game/.editor/watch-timing.txt");
        next.Begin();
        var shown = Show(next, Step(BuildStage.Assets, 0, 10));

        await Assert.That(shown.Fraction).IsEqualTo(0.2).Within(1e-9);
    }

    [Test]
    public async Task a_damaged_history_file_is_no_history()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/.editor");
        fileSystem.WriteAllText("/game/.editor/watch-timing.txt", "not numbers\n1 2\n-1 1 1 1\nNaN 1 1 1\n");

        var progress = new RebuildProgress(new ManualTime(), fileSystem, "/game/.editor/watch-timing.txt");
        progress.Begin();
        var shown = Show(progress, Step(BuildStage.Assets, 50, 100));

        var weights = RebuildProgress.DefaultWeights;
        await Assert.That(shown.Fraction).IsEqualTo((weights[0] + weights[1] + weights[2] * 0.5) / weights.Sum()).Within(1e-9);
    }

    /// <summary>One rebuild is its own median; a single cold one would leave the next incremental rebuild stuck near zero, so it is not trusted yet.</summary>
    [Test]
    public async Task one_recorded_rebuild_is_not_enough_to_weigh_the_next()
    {
        var time = new ManualTime();
        var progress = new RebuildProgress(time);
        RunThrough(progress, time, 2, 10, 600, 0);

        progress.Begin();
        var half = Show(progress, Step(BuildStage.Assets, 50, 100));

        var weights = RebuildProgress.DefaultWeights;
        await Assert.That(half.Fraction).IsEqualTo((weights[0] + weights[1] + weights[2] * 0.5) / weights.Sum()).Within(1e-9);
    }

    /// <summary>A walk that ended in errors never ran Finish; the store says so, and the next watcher reads it back that way.</summary>
    [Test]
    public async Task an_unreached_stage_is_stored_as_unreached()
    {
        using var fileSystem = new MemoryFileSystem();
        var time = new ManualTime();
        var first = new RebuildProgress(time, fileSystem, "/game/.editor/watch-timing.txt");
        RunThrough(first, time, 1, 1, 8);
        RunThrough(first, time, 1, 1, 8);

        await Assert.That(fileSystem.ReadAllText("/game/.editor/watch-timing.txt")).IsEqualTo("1 1 8 -\n1 1 8 -\n");

        var next = new RebuildProgress(time, fileSystem, "/game/.editor/watch-timing.txt");
        next.Begin();
        var shown = Show(next, Step(BuildStage.Assets, 0, 10));

        await Assert.That(shown.Fraction).IsEqualTo(0.2).Within(1e-9);
    }
}

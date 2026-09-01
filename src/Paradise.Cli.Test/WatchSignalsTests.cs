namespace Paradise.Cli.Test;

/// <summary>
/// The coordinator's sequential contracts. The interleavings live in
/// <c>Paradise.Cli.CoyoteTest</c>; these pin what a single thread is allowed to observe.
/// </summary>
public class WatchSignalsTests
{
    [Test]
    public async Task consume_rebuild_is_one_shot()
    {
        using var signals = new WatchSignals();
        await Assert.That(signals.ConsumeRebuild()).IsFalse();
        signals.RequestRebuild();
        await Assert.That(signals.ConsumeRebuild()).IsTrue();
        await Assert.That(signals.ConsumeRebuild()).IsFalse();
    }

    [Test]
    public async Task two_rebuilds_before_consume_collapse_to_one()
    {
        using var signals = new WatchSignals();
        signals.RequestRebuild();
        signals.RequestRebuild();
        await Assert.That(signals.ConsumeRebuild()).IsTrue();
        await Assert.That(signals.ConsumeRebuild()).IsFalse();
    }

    [Test]
    public async Task stop_is_idempotent_and_visible()
    {
        using var signals = new WatchSignals();
        await Assert.That(signals.IsStopping).IsFalse();
        signals.RequestStop();
        signals.RequestStop();
        await Assert.That(signals.IsStopping).IsTrue();
    }

    [Test]
    public async Task wait_returns_immediately_when_already_stopping()
    {
        using var signals = new WatchSignals();
        signals.RequestStop();
        var started = DateTime.UtcNow;
        await signals.WaitQuietAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That((DateTime.UtcNow - started).TotalSeconds).IsLessThan(1);
    }

    [Test]
    public async Task a_rebuild_pulse_wakes_a_long_wait()
    {
        using var signals = new WatchSignals();
        var waiting = signals.WaitQuietAsync(TimeSpan.FromSeconds(10));
        signals.RequestRebuild();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(signals.ConsumeRebuild()).IsTrue();
    }
}

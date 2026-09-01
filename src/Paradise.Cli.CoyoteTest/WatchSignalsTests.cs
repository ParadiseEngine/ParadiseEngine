using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Cli.CoyoteTest;

/// <summary>
/// <see cref="WatchSignals"/> under systematic exploration.
///
/// Two threads: the watch loop waits and consumes; the tray (or Ctrl+C) requests stop and
/// rebuild. Native notify-icon calls are not in this type, which is the whole reason it exists
/// as its own type — Coyote cannot schedule <c>GetMessage</c>.
///
/// THE INVARIANT, in one sentence: a rebuild that was requested is still pending until the loop
/// consumes it, and a stop that was requested is visible as <see cref="WatchSignals.IsStopping"/>
/// without the wait having to finish its debounce. A lost rebuild is a menu click that did
/// nothing; a lost stop is a tray that cannot quit the process.
/// </summary>
public static class WatchSignalsTests
{
    /// <summary>
    /// Rebuild racing a wait: the loop must see the request, either because the pulse woke it
    /// or because the flag survived a lost wakeup.
    /// </summary>
    [Test]
    public static async Task RebuildRacingWait_IsNotLost()
    {
        using var signals = new WatchSignals();
        var consumedDuringWait = 0;

        var waiter = Task.Run(async () =>
        {
            await signals.WaitQuietAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            if (signals.ConsumeRebuild())
            {
                Interlocked.Exchange(ref consumedDuringWait, 1);
            }
        });

        var requester = Task.Run(() => signals.RequestRebuild());

        await Task.WhenAll(waiter, requester).ConfigureAwait(false);

        var leftover = signals.ConsumeRebuild();
        Specification.Assert(consumedDuringWait == 1 || leftover,
            "A rebuild requested around a wait must still be pending: either the wait woke and consumed it, or the flag remains for the next loop.");
    }

    /// <summary>
    /// Stop racing a wait: the wait must return, and the loop must see that it is stopping.
    /// </summary>
    [Test]
    public static async Task StopRacingWait_IsVisibleAndUnblocks()
    {
        using var signals = new WatchSignals();

        var waiter = Task.Run(async () =>
        {
            await signals.WaitQuietAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        });

        var stopper = Task.Run(() => signals.RequestStop());

        await Task.WhenAll(waiter, stopper).ConfigureAwait(false);

        Specification.Assert(signals.IsStopping,
            "RequestStop must leave IsStopping set, or the loop would wait another debounce after the tray said stop.");
    }

    /// <summary>
    /// Rebuild requested before the wait starts: the flag is already set, so even a wait that
    /// never sees the pulse still consumes a rebuild. This is the "click Rebuild, then the loop
    /// enters WaitQuiet" order.
    /// </summary>
    [Test]
    public static async Task RebuildBeforeWait_SurvivesAsAFlag()
    {
        using var signals = new WatchSignals();
        signals.RequestRebuild();

        await signals.WaitQuietAsync(TimeSpan.Zero).ConfigureAwait(false);

        Specification.Assert(signals.ConsumeRebuild(),
            "A rebuild requested before the wait must not be eaten by the wait itself.");
    }

    /// <summary>
    /// Two rebuilds racing a consume: collapsing to one rebuild is allowed (the loop rebuilds
    /// the whole tree, so a second click during the wait is the same work). Losing both is not.
    /// </summary>
    [Test]
    public static async Task ConcurrentRebuilds_AreNotBothLost()
    {
        using var signals = new WatchSignals();

        var first = Task.Run(() => signals.RequestRebuild());
        var second = Task.Run(() => signals.RequestRebuild());
        await Task.WhenAll(first, second).ConfigureAwait(false);

        Specification.Assert(signals.ConsumeRebuild(),
            "Two rebuild clicks must leave at least one request for the loop to honour.");
        Specification.Assert(!signals.ConsumeRebuild(),
            "ConsumeRebuild is one-shot: a second consume without a new request must be false.");
    }
}

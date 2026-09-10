using Microsoft.Coyote.Specifications;

namespace Paradise.Cli.CoyoteTest;

public static class TrayTaskStateTests
{
    private static TrayTaskState Create() => new("compile", false, TimeSpan.Zero);

    public static async Task ChangeDuringCompile_IsNotLost()
    {
        var state = Create();
        state.Request("compile");
        Specification.Assert(state.TryBegin(DateTimeOffset.UnixEpoch) == "compile", "The initial manual compile must start.");
        await Task.WhenAll(
            Task.Run(() => state.Observe(DateTimeOffset.UnixEpoch)),
            Task.Run(() => state.Complete(0)),
            Task.Run(() => state.ToggleAutoWatch())).ConfigureAwait(false);
        // Enabling always catches up, even if the earlier event arrived while disabled.
        Specification.Assert(state.TryBegin(DateTimeOffset.MaxValue) == "compile", "A save/enable racing completion must leave a follow-up compile pending.");
    }

    public static async Task TwoConsumers_StartAtMostOneTask()
    {
        var state = Create();
        state.Request("compile");
        var started = 0;
        await Task.WhenAll(Task.Run(Consume), Task.Run(Consume)).ConfigureAwait(false);
        Specification.Assert(started == 1, "Only one worker can own a task.");
        void Consume()
        {
            if (state.TryBegin(DateTimeOffset.UnixEpoch) is not null) Interlocked.Increment(ref started);
        }
    }

    public static async Task DisableWatch_PreservesManualRequest()
    {
        var state = Create();
        state.ToggleAutoWatch();
        await Task.WhenAll(
            Task.Run(() => state.Request("check")),
            Task.Run(() => state.ToggleAutoWatch())).ConfigureAwait(false);
        Specification.Assert(state.TryBegin(DateTimeOffset.MaxValue) == "check", "Disabling auto-watch must not discard an explicit Check.");
        state.Complete(0);
        Specification.Assert(state.TryBegin(DateTimeOffset.MaxValue) is null, "The discarded automatic compile must not restart.");
    }

    public static async Task Stop_PreventsFurtherStarts()
    {
        var state = Create();
        state.ToggleAutoWatch();
        await Task.WhenAll(Task.Run(() => state.Request("compile")), Task.Run(state.Stop)).ConfigureAwait(false);
        Specification.Assert(state.TryBegin(DateTimeOffset.MaxValue) is null, "Stop must discard both automatic and manual work.");
    }

    public static async Task Cancellation_IsVisibleToTheWorker()
    {
        var state = Create();
        state.Request("compile");
        state.TryBegin(DateTimeOffset.UnixEpoch);
        await Task.WhenAll(Task.Run(state.Cancel), Task.Run(() => _ = state.Snapshot)).ConfigureAwait(false);
        Specification.Assert(state.Snapshot.CancelRequested, "A cancellation cannot be consumed by a status read.");
        state.Complete(130);
        Specification.Assert(!state.Snapshot.Running, "Completion must release the running task.");
    }
}

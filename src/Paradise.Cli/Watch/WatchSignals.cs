namespace Paradise.Cli;

/// <summary>
/// The watch loop's side of the tray (and of Ctrl+C): stop, rebuild-now, and the wake that
/// makes those interrupt the debounce wait.
/// </summary>
/// <remarks>
/// <para>
/// Two threads touch this. The watch loop waits and consumes; the tray message pump (and the
/// console cancel handler) request. Native notify-icon calls stay out of here on purpose —
/// Coyote can schedule this, and it cannot schedule <c>GetMessage</c>.
/// </para>
/// <para>
/// <b>A rebuild request is a flag, not an edge.</b> The wake can be lost if it fires between
/// waits; the flag cannot. Missing the pulse costs at most one debounce before the loop sees
/// the request. Missing the flag would cost the rebuild the author asked for.
/// </para>
/// <para>
/// The lock is <c>object</c> and not <c>System.Threading.Lock</c>, for the same Coyote reason
/// as <see cref="Paradise.Assets.Pipeline.AssetWatcher"/> — 1.7.11 rewrites <c>Monitor</c>
/// and does not intercept <c>Lock.EnterScope</c>.
/// </para>
/// </remarks>
internal sealed class WatchSignals : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _wake = NewWake();
    private int _rebuild;

    /// <summary>Cancelled once, by <see cref="RequestStop"/> or by disposing.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>Whether <see cref="RequestStop"/> has been called.</summary>
    public bool IsStopping => _stopping.IsCancellationRequested;

    /// <summary>End the watch. Idempotent: a second stop is a no-op, matching Ctrl+C then tray-Stop.</summary>
    public void RequestStop()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Pulse();
    }

    /// <summary>Ask the loop to rebuild as soon as it is between waits, even with no file change.</summary>
    public void RequestRebuild()
    {
        Interlocked.Exchange(ref _rebuild, 1);
        Pulse();
    }

    /// <summary>Take the rebuild request if one is pending. The loop calls this once per wake.</summary>
    public bool ConsumeRebuild() => Interlocked.Exchange(ref _rebuild, 0) != 0;

    /// <summary>
    /// Block until the quiet window elapses, or until stop/rebuild pulses. Returns early when
    /// already stopping, so a stop requested before the wait is not delayed by a full debounce.
    /// </summary>
    public Task WaitQuietAsync(TimeSpan debounce) => WaitQuietAsync(debounce, CancellationToken.None);

    /// <inheritdoc cref="WaitQuietAsync(TimeSpan)"/>
    public async Task WaitQuietAsync(TimeSpan debounce, CancellationToken cancellationToken)
    {
        if (_stopping.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;

        Task wake;
        lock (_gate)
        {
            wake = _wake.Task;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        try
        {
            var delay = Task.Delay(debounce, linked.Token);
            var done = await Task.WhenAny(wake, delay).ConfigureAwait(false);
            if (done == delay)
            {
                await delay.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop, or the caller cancelled. Either way the wait is over.
        }
    }

    /// <summary>Synchronous <see cref="WaitQuietAsync(TimeSpan)"/> for the console loop.</summary>
    public void WaitQuiet(TimeSpan debounce) => WaitQuietAsync(debounce).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    private void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _wake;
            _wake = NewWake();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource NewWake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

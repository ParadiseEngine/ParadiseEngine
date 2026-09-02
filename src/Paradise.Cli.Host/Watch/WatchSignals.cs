namespace Paradise.Cli;

/// <summary>The watch loop's side of the tray and Ctrl+C: stop, rebuild-now, and the wake that interrupts the debounce wait.</summary>
/// <remarks>
/// Native notify-icon calls stay out of here so Coyote can schedule this class. A rebuild request
/// is a flag, not an edge: a lost pulse costs one debounce, a lost flag costs the rebuild. The
/// lock is <c>object</c>, not <c>System.Threading.Lock</c>, for the Coyote reason in
/// <see cref="Paradise.Assets.Pipeline.AssetWatcher"/>.
/// </remarks>
internal sealed class WatchSignals : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _wake = NewWake();
    private int _rebuild;

    public CancellationToken Stopping => _stopping.Token;

    public bool IsStopping => _stopping.IsCancellationRequested;

    /// <summary>Idempotent: Ctrl+C then tray-Stop is normal.</summary>
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

    public void RequestRebuild()
    {
        Interlocked.Exchange(ref _rebuild, 1);
        Pulse();
    }

    public bool ConsumeRebuild() => Interlocked.Exchange(ref _rebuild, 0) != 0;

    /// <summary>Returns early when already stopping, so a stop requested before the wait is not delayed by a full debounce.</summary>
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
        }
    }

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

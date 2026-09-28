using Paradise.Assets.Pipeline;

namespace Paradise.Cli;

/// <summary>Coordinates watch drains and rebuild requests through testable callbacks.</summary>
internal sealed class WatchSession
{
    private readonly WatchSignals _signals;
    private readonly IWatchTray _tray;
    private readonly Func<int> _drain;
    private readonly Func<Action<BuildProgress>, BuildResult>? _rebuild;
    private readonly Action<string> _log;
    private readonly Action<string> _error;
    private readonly Func<string> _outputDisplay;
    private readonly TimeSpan _quiet;
    private readonly RebuildProgress _progress;
    private readonly TimeSpan _refresh;

    // Serializes tray progress updates from the building thread (stage changes) and the refresh
    // timer, so an older estimate cannot land after a newer one.
    private readonly object _publishGate = new();

    // 1 once a refresh failed in the current rebuild, so its warning is written once.
    private int _refreshFailed;

    /// <summary>How often the tray's estimate refreshes during a rebuild by default: often enough
    /// that the bar moves through one long step, far below what AppKit or the shell would notice.</summary>
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromMilliseconds(100);

    /// <param name="rebuild">Builds, telling the callback it is given where the build is.</param>
    /// <param name="progress">Estimates what the tray shows while a rebuild runs; one on the system clock by default.</param>
    /// <param name="refresh">How often that estimate is re-sent during a rebuild (<see cref="DefaultRefresh"/> by default); a new stage is always sent at once. <see cref="Timeout.InfiniteTimeSpan"/> sends stage changes only.</param>
    public WatchSession(
        WatchSignals signals,
        IWatchTray tray,
        Func<int> drain,
        Func<Action<BuildProgress>, BuildResult>? rebuild,
        Action<string> log,
        Action<string> error,
        Func<string> outputDisplay,
        TimeSpan quiet,
        RebuildProgress? progress = null,
        TimeSpan? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(outputDisplay);

        _signals = signals;
        _tray = tray;
        _drain = drain;
        _rebuild = rebuild;
        _log = log;
        _error = error;
        _outputDisplay = outputDisplay;
        _quiet = quiet;
        _progress = progress ?? new RebuildProgress(TimeProvider.System);
        _refresh = refresh ?? DefaultRefresh;
    }

    public int LastErrorCount { get; private set; }

    public WatchStatus Status { get; private set; } = WatchStatus.Alive;

    public void Run()
    {
        Set(WatchStatus.Alive, 0);

        while (!_signals.IsStopping)
        {
            _signals.WaitQuiet(_quiet);
            if (_signals.IsStopping) break;

            var rebuildNow = _signals.ConsumeRebuild();
            var drained = _drain();
            if (_rebuild is null) continue;
            if (!rebuildNow && drained == 0) continue;

            Set(WatchStatus.Building, LastErrorCount);
            _progress.Begin();
            Volatile.Write(ref _refreshFailed, 0);
            // A timer rather than refreshing on reports: one step can take a minute (a model
            // conversion), and the estimate has to keep moving through it.
            var refresher = new Timer(static session => ((WatchSession)session!).Refresh(), this, _refresh, _refresh);
            BuildResult result;
            try
            {
                result = Rebuild();
            }
            finally
            {
                // Waits out a tick in flight, which would otherwise show progress after the clear.
                using (var stopped = new ManualResetEvent(false))
                {
                    if (refresher.Dispose(stopped)) stopped.WaitOne();
                }

                try
                {
                    _progress.End();
                }
                finally
                {
                    lock (_publishGate) _tray.SetProgress(null);
                }
            }

            LastErrorCount = result.Errors.Count;
            foreach (var error in result.Errors) _error($"error: {error}");
            _log(result.Succeeded
                ? $"watch: rebuilt {result.AssetCount} asset(s) into {_outputDisplay()}"
                : $"watch: build FAILED with {result.Errors.Count} error(s)");
            Set(result.Succeeded ? WatchStatus.Idle : WatchStatus.Failed, LastErrorCount);
        }
    }

    /// <summary>The runner catches what it can; this catches the rest, because a watch that dies mid-edit reports nothing and rebuilds nothing (#203).</summary>
    private BuildResult Rebuild()
    {
        try
        {
            return _rebuild!(Report);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new BuildResult(false, [$"rebuild threw {error.GetType().Name}: {error.Message}"], 0, default);
        }
    }

    private void Report(BuildProgress report)
    {
        if (_progress.Report(report)) Publish();
    }

    /// <summary>A timer tick: an exception escaping a timer callback ends the process, and the
    /// icon is a satellite of the watch, never a reason to stop it.</summary>
    private void Refresh()
    {
        try
        {
            Publish();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Once per rebuild: the timer would repeat it ten times a second.
            if (Interlocked.Exchange(ref _refreshFailed, 1) == 0)
            {
                _error($"warning: the tray could not show rebuild progress: {error.Message}");
            }
        }
    }

    private void Publish()
    {
        lock (_publishGate)
        {
            if (_progress.Snapshot() is { } shown) _tray.SetProgress(shown);
        }
    }

    private void Set(WatchStatus status, int errorCount)
    {
        Status = status;
        _tray.SetState(status, errorCount);
    }
}

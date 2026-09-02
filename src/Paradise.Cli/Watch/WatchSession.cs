using Paradise.Assets.Pipeline;

namespace Paradise.Cli;

/// <summary>The watch loop, extracted from the verb so it can be driven with a fake drain and a recording tray. Rebuilds only when the drain touched a sidecar — issue #195.</summary>
internal sealed class WatchSession
{
    private readonly WatchSignals _signals;
    private readonly IWatchTray _tray;
    private readonly Func<int> _drain;
    private readonly Func<BuildResult>? _rebuild;
    private readonly Action<string> _log;
    private readonly Action<string> _error;
    private readonly Func<string> _outputDisplay;
    private readonly TimeSpan _quiet;

    public WatchSession(
        WatchSignals signals,
        IWatchTray tray,
        Func<int> drain,
        Func<BuildResult>? rebuild,
        Action<string> log,
        Action<string> error,
        Func<string> outputDisplay,
        TimeSpan quiet)
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
            var result = _rebuild();
            LastErrorCount = result.Errors.Count;
            foreach (var error in result.Errors) _error($"error: {error}");
            _log(result.Succeeded
                ? $"watch: rebuilt {result.AssetCount} asset(s) into {_outputDisplay()}"
                : $"watch: build FAILED with {result.Errors.Count} error(s)");
            Set(result.Succeeded ? WatchStatus.Idle : WatchStatus.Failed, LastErrorCount);
        }
    }

    private void Set(WatchStatus status, int errorCount)
    {
        Status = status;
        _tray.SetState(status, errorCount);
    }
}

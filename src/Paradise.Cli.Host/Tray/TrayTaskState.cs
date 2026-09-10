namespace Paradise.Cli;

internal sealed record TrayTaskSnapshot(bool AutoWatch, bool WatchAvailable, bool Running, bool CancelRequested, string Status);

/// <summary>Serializes menu requests and file events without putting compiler work on the native UI thread.</summary>
internal sealed class TrayTaskState
{
    // Coyote schedules Monitor, not System.Threading.Lock.
    private readonly object _gate = new();
    private readonly string _automaticTask;
    private readonly TimeSpan _debounce;
    private bool _watching;
    private bool _watchAvailable = true;
    private bool _automaticPending;
    private DateTimeOffset _due;
    private string? _manualPending;
    private string? _running;
    private bool _cancel;
    private bool _stopped;
    private string _status = "Ready — no task run yet";

    public TrayTaskState(string automaticTask, bool autoWatch, TimeSpan debounce)
    {
        _automaticTask = automaticTask;
        _watching = autoWatch;
        _automaticPending = autoWatch;
        _debounce = debounce;
    }

    public TrayTaskSnapshot Snapshot
    {
        get { lock (_gate) return new(_watching, _watchAvailable, _running is not null, _cancel, _status); }
    }

    public void ToggleAutoWatch()
    {
        lock (_gate)
        {
            if (_stopped || !_watchAvailable) return;
            _watching = !_watching;
            _automaticPending = _watching;
            _due = DateTimeOffset.MinValue;
        }
    }

    public void DisableAutoWatch(string reason)
    {
        lock (_gate)
        {
            _watchAvailable = false;
            _watching = false;
            _automaticPending = false;
            _status = $"Auto-watch unavailable: {reason}";
        }
    }

    public void Observe(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_watching || _stopped || _cancel) return;
            _automaticPending = true;
            _due = now + _debounce;
        }
    }

    public bool Request(string task)
    {
        lock (_gate)
        {
            if (_stopped || _running is not null || _manualPending is not null) return false;
            _manualPending = task;
            _status = $"{task}: queued";
            return true;
        }
    }

    public string? TryBegin(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_stopped || _running is not null) return null;
            var task = _manualPending;
            if (task is null && _automaticPending && now >= _due) task = _automaticTask;
            if (task is null) return null;
            _manualPending = null;
            if (task == _automaticTask) _automaticPending = false;
            _running = task;
            _cancel = false;
            _status = $"{task}: running…";
            return task;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _automaticPending = false;
            _manualPending = null;
            if (_running is not null)
            {
                _cancel = true;
                _status = $"{_running}: cancelling…";
            }
        }
    }

    public void Complete(int exitCode)
    {
        lock (_gate)
        {
            if (_running is null) throw new InvalidOperationException("No tray task is running.");
            _status = _stopped ? "Stopped" : _cancel || exitCode == 130 ? $"{_running}: cancelled"
                : exitCode == 0 ? $"{_running}: succeeded" : $"{_running}: failed (exit {exitCode}) — see console log";
            _running = null;
            _cancel = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _automaticPending = false;
            _manualPending = null;
            _cancel = _running is not null;
            _status = "Stopped";
        }
    }
}

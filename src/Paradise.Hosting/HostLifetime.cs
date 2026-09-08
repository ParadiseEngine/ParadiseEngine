namespace Paradise.Hosting;

/// <summary>Coordinates shutdown without coupling the ordering rules to native threads.</summary>
internal sealed class HostLifetime
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _simulationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _presentationStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopRequested;
    private Exception? _failure;
    private (uint Width, uint Height)? _resize;

    internal bool StopRequested
    {
        get
        {
            lock (_gate)
            {
                return _stopRequested;
            }
        }
    }

    internal Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    internal Task<bool> SimulationReady => _simulationReady.Task;
    internal Task PresentationStopped => _presentationStopped.Task;

    internal void MarkSimulationReady() => _simulationReady.TrySetResult(true);
    internal void MarkSimulationUnavailable() => _simulationReady.TrySetResult(false);
    internal void MarkPresentationStopped() => _presentationStopped.TrySetResult();

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
        }
    }

    internal void Fail(Exception exception)
    {
        lock (_gate)
        {
            _failure ??= exception;
            _stopRequested = true;
        }
    }

    internal void Resize(uint width, uint height)
    {
        lock (_gate)
        {
            _resize = (width, height);
        }
    }

    internal bool TryTakeResize(out uint width, out uint height)
    {
        lock (_gate)
        {
            if (_resize is not { } size)
            {
                width = height = 0;
                return false;
            }

            (width, height) = size;
            _resize = null;
            return true;
        }
    }
}

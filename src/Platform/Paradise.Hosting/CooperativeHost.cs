using Microsoft.Extensions.Logging;
using Paradise.Features;
using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>Runs an application on the caller's thread, one frame per <see cref="Frame"/> call.</summary>
/// <remarks>
/// <para>For hosts that own the loop and cannot block or start threads: the browser's
/// <c>requestAnimationFrame</c>, mobile lifecycle callbacks, an editor embedding a game view.
/// <see cref="ParadiseHost.Run"/> is the desktop driver, which blocks the main thread on the window
/// pump and runs simulation and presentation on two threads; both drivers share the fixed-step
/// stepping and <see cref="HostClock"/>, so an application behaves the same under either.</para>
/// <para>Simulation and presentation are created, called and disposed on the caller's thread, in the
/// same order as the threaded host: simulation first, presentation second, then each frame delivers
/// input and the owed ticks before rendering; disposal releases presentation before simulation, then
/// the window and the platform. A frame never waits for a capture: a pending capture holds back the
/// next one and, once the run stops, holds back disposal until it completes or exceeds
/// <see cref="HostOptions.ShutdownTimeout"/>.</para>
/// </remarks>
public sealed partial class CooperativeHost : IDisposable
{
    private readonly IWindowPlatform _platform;
    private readonly IWindow _window;
    private readonly HostOptions _options;
    private readonly ILogger _logger;
    private readonly IHostSimulation _simulation;
    private readonly IHostPresentation _presentation;
    private readonly SimulationStepper _stepper;
    private readonly IEnumerator<int>? _captureSchedule;
    private readonly Func<bool> _stopRequested;
    private bool _hasCapture;
    private int _captured;
    private Task? _pendingCapture;
    private TimeSpan _captureStarted;
    private bool _captureAbandoned;
    private (uint Width, uint Height)? _resize;
    private TimeSpan _previousFrame;
    private int _frames;
    private bool _stopRequestedFlag;
    private bool _disposed;

    private CooperativeHost(
        IWindowPlatform platform, IWindow window, HostContext context, HostOptions options, ILogger logger,
        IHostSimulation simulation, IHostPresentation presentation, SimulationStepper stepper)
    {
        _platform = platform;
        _window = window;
        Context = context;
        _options = options;
        _logger = logger;
        _simulation = simulation;
        _presentation = presentation;
        _stepper = stepper;
        _stopRequested = () => _stopRequestedFlag;
        _captureSchedule = options.Capture?.Schedule().GetEnumerator();
        _hasCapture = _captureSchedule?.MoveNext() ?? false;
        _previousFrame = context.Clock.Elapsed;
        _window.Resized += OnResized;
    }

    /// <summary>The window, configuration and clock the application was created with.</summary>
    public HostContext Context { get; }

    /// <summary>Frames rendered so far.</summary>
    public int Frames => _frames;

    /// <summary>The first failure that stopped the run, or null.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>True until the run has stopped and no capture is still writing within its time budget.</summary>
    public bool IsRunning => !_stopRequestedFlag || (_pendingCapture is { IsCompleted: false } && !_captureAbandoned);

    /// <summary>Creates the window, simulation and presentation on the calling thread. The started host owns
    /// <paramref name="platform"/> and releases it on <see cref="Dispose"/>.</summary>
    /// <exception cref="Exception">Whatever creation threw, after the window, simulation and presentation already
    /// created were released; the platform then stays the caller's.</exception>
    public static CooperativeHost Start(
        IHostApplication application, IWindowPlatform platform, HostOptions options, FeatureSwitches switches, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(logger);
        ParadiseHost.Validate(options);

        IWindow? window = null;
        IHostSimulation? simulation = null;
        IHostPresentation? presentation = null;
        try
        {
            window = platform.CreateWindow(options.Window);
            var surface = window.CreateSurface();
            var context = new HostContext(window, switches, window.Width, window.Height);
            simulation = application.CreateSimulation(context);
            var stepper = new SimulationStepper(simulation, context, options);
            presentation = application.CreatePresentation(context, in surface);
            return new CooperativeHost(platform, window, context, options, logger, simulation, presentation, stepper);
        }
        catch
        {
            presentation?.Dispose();
            simulation?.Dispose();
            window?.Dispose();
            throw;
        }
    }

    /// <summary>Advances one frame: pumps the platform, delivers input and the owed ticks, applies the
    /// newest resize, starts a scheduled capture and renders.</summary>
    /// <returns><see cref="IsRunning"/>: false once the window closed, the frame limit was reached or a
    /// callback failed, and any capture finished.</returns>
    public bool Frame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObserveCapture();
        if (_stopRequestedFlag) return IsRunning;

        try
        {
            _platform.Pump();
            if (_window.CloseRequested)
            {
                Stop();
                return IsRunning;
            }

            _stepper.Step(_stopRequested);

            if (_resize is { } size)
            {
                _resize = null;
                _presentation.Resize(size.Width, size.Height);
            }

            var nextFrame = _frames + 1;
            if (_hasCapture && _pendingCapture is null && _captureSchedule!.Current == nextFrame)
            {
                _pendingCapture = _presentation.CaptureAsync(_options.Capture!.PathFor(nextFrame));
                _captureStarted = Context.Clock.Elapsed;
            }

            var now = Context.Clock.Elapsed;
            _presentation.Render(now, now - _previousFrame);
            _previousFrame = now;
            _frames++;
            if (_options.FrameLimit is { } limit && _frames >= limit) Stop();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }

        ObserveCapture();
        return IsRunning;
    }

    /// <summary>Stops the run; the next <see cref="Frame"/> renders nothing.</summary>
    public void RequestStop() => Stop();

    /// <summary>Releases presentation, then simulation, then the window and the platform.</summary>
    /// <remarks>A capture that outlived <see cref="HostOptions.ShutdownTimeout"/> may still read renderer
    /// data, so its resources are retained rather than released, as the threaded host does.</remarks>
    /// <exception cref="InvalidOperationException">A capture is still writing within its time budget;
    /// nothing is released. Call again once <see cref="IsRunning"/> turns false.</exception>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        ObserveCapture();
        if (_pendingCapture is { IsCompleted: false })
        {
            if (!_captureAbandoned)
                throw new InvalidOperationException("A capture is still writing; dispose the host after IsRunning turns false.");

            _disposed = true;
            LogFailure(_logger, Failure!);
            return;
        }

        _disposed = true;
        _window.Resized -= OnResized;
        _captureSchedule?.Dispose();
        Release(_presentation);
        Release(_simulation);
        Release(_window);
        Release(_platform);
        if (Failure is not null) LogFailure(_logger, Failure);
    }

    private void ObserveCapture()
    {
        if (_pendingCapture is not { } capture) return;
        if (!capture.IsCompleted)
        {
            if (!_captureAbandoned && Context.Clock.Elapsed - _captureStarted > _options.ShutdownTimeout)
            {
                // Abandon the writer without releasing what it may still read.
                _captureAbandoned = true;
                Fail(new TimeoutException("A capture did not complete within the shutdown budget."));
            }

            return;
        }

        _pendingCapture = null;
        if (capture.Exception is { } failure)
        {
            Fail(failure.InnerExceptions.Count == 1 ? failure.InnerExceptions[0] : failure);
            return;
        }

        if (capture.IsCanceled)
        {
            Fail(new TaskCanceledException(capture));
            return;
        }

        _captured++;
        _hasCapture = _captureSchedule!.MoveNext();
        if (_stopRequestedFlag) CheckCapturesComplete();
    }

    private void Stop()
    {
        if (_stopRequestedFlag) return;
        _stopRequestedFlag = true;
        if (_pendingCapture is null) CheckCapturesComplete();
    }

    private void CheckCapturesComplete()
    {
        if (Failure is null && _captured < (_options.Capture?.Frames.Count ?? 0))
            Failure = new InvalidOperationException("The application stopped before all requested frames were captured.");
    }

    private void Fail(Exception exception)
    {
        Failure ??= exception;
        _stopRequestedFlag = true;
    }

    private void Release(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            Failure ??= exception;
        }
    }

    private void OnResized(uint width, uint height) => _resize = (width, height);

    [LoggerMessage(1, LogLevel.Error, "Application host failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

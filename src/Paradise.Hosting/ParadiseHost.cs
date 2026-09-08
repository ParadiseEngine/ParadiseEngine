using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Paradise.Features;
using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>Runs an application's simulation, presentation and native window lifetime.</summary>
public static partial class ParadiseHost
{
    /// <summary>Owns the platform and runs its window pump on the calling main thread.</summary>
    /// <remarks>Application components are created and disposed on their owning workers.
    /// A worker that cannot stop within the shutdown budget retains resources it may still use;
    /// the host reports failure instead of destroying live native state.</remarks>
    public static int Run(
        IHostApplication application,
        IWindowPlatform platform,
        HostOptions options,
        FeatureSwitches switches,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(logger);

        var lifetime = new HostLifetime();
        IWindow? window = null;
        Thread? simulation = null;
        Thread? presentation = null;
        var simulationStarted = false;
        var presentationStarted = false;
        try
        {
            Validate(options);
            window = platform.CreateWindow(options.Window);
            var surface = window.CreateSurface();
            var context = new HostContext(window, switches, window.Width, window.Height);
            window.Resized += lifetime.Resize;
            simulation = new Thread(() => RunSimulation(application, context, options, lifetime))
            {
                IsBackground = true,
                Name = "Paradise simulation",
            };
            presentation = new Thread(() => RunPresentation(application, context, surface, options, lifetime))
            {
                IsBackground = true,
                Name = "Paradise presentation",
            };
            simulation.Start();
            simulationStarted = true;
            presentation.Start();
            presentationStarted = true;

            while (!lifetime.StopRequested && !window.CloseRequested)
            {
                platform.Pump();
                Thread.Sleep(1);
            }
        }
        catch (Exception exception)
        {
            lifetime.Fail(exception);
        }
        finally
        {
            lifetime.RequestStop();
            if (window is not null)
            {
                window.Resized -= lifetime.Resize;
            }

            if (!simulationStarted)
            {
                lifetime.MarkSimulationUnavailable();
            }

            if (!presentationStarted)
            {
                lifetime.MarkPresentationStopped();
            }

            var shutdown = Stopwatch.StartNew();
            var presentationJoined = !presentationStarted || Join(presentation!, shutdown, options.ShutdownTimeout);
            var simulationJoined = !simulationStarted || Join(simulation!, shutdown, options.ShutdownTimeout);
            if (presentationJoined && simulationJoined)
            {
                Dispose(window, lifetime);
                Dispose(platform, lifetime);
            }
            else
            {
                lifetime.Fail(new TimeoutException(
                    "Application workers did not stop within the shutdown budget; their window and platform were retained."));
            }
        }

        if (lifetime.Failure is { } failure)
        {
            LogFailure(logger, failure);
            return 1;
        }

        return 0;
    }

    private static void Validate(HostOptions options)
    {
        if (options.FrameLimit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxCatchUp, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ShutdownTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ShutdownTimeout, TimeSpan.FromMilliseconds(int.MaxValue));
        if (options.Capture is { } capture)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capture.Path);
            ArgumentNullException.ThrowIfNull(capture.Frames);
            if (capture.EveryFrames is { } interval)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
            }

            var previous = 0;
            foreach (var frame in capture.Frames)
            {
                if (frame <= previous)
                {
                    throw new ArgumentException("Capture frames must be positive and strictly increasing.", nameof(options));
                }

                previous = frame;
            }

            if (options.FrameLimit is { } frames && previous > frames)
            {
                throw new ArgumentException("The frame limit ends before the final requested capture.", nameof(options));
            }
        }
    }

    private static void RunSimulation(
        IHostApplication application,
        HostContext context,
        HostOptions options,
        HostLifetime lifetime)
    {
        IHostSimulation? simulation = null;
        try
        {
            if (lifetime.StopRequested)
            {
                return;
            }

            simulation = application.CreateSimulation(context);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(simulation.FixedStep, TimeSpan.Zero);
            var clock = new FixedStepClock(simulation.FixedStep, options.MaxCatchUp);
            var elapsed = Stopwatch.StartNew();
            var previous = elapsed.Elapsed;
            lifetime.MarkSimulationReady();
            while (!lifetime.StopRequested)
            {
                var now = elapsed.Elapsed;
                var ticks = clock.Advance(now - previous);
                previous = now;
                if (ticks == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                while (context.Window.TryReadEvent(out var input))
                {
                    simulation.HandleInput(in input);
                }

                for (var i = 0; i < ticks && !lifetime.StopRequested; i++)
                {
                    simulation.Tick();
                }

                simulation.AfterTicks();
            }
        }
        catch (Exception exception)
        {
            lifetime.Fail(exception);
        }
        finally
        {
            lifetime.MarkSimulationUnavailable();
            lifetime.RequestStop();
            // The renderer may still be constructing, capturing, or returning borrowed snapshots.
            lifetime.PresentationStopped.GetAwaiter().GetResult();
            Dispose(simulation, lifetime);
        }
    }

    private static void RunPresentation(
        IHostApplication application,
        HostContext context,
        SurfaceDescriptor surface,
        HostOptions options,
        HostLifetime lifetime)
    {
        IHostPresentation? presentation = null;
        Task? pendingCapture = null;
        IEnumerator<int>? schedule = null;
        var captured = 0;
        try
        {
            schedule = options.Capture?.Schedule().GetEnumerator();
            var hasCapture = schedule?.MoveNext() ?? false;
            if (!lifetime.SimulationReady.GetAwaiter().GetResult() || lifetime.StopRequested)
            {
                return;
            }

            presentation = application.CreatePresentation(context, in surface);
            var clock = Stopwatch.StartNew();
            var previous = TimeSpan.Zero;
            var frames = 0;
            while (!lifetime.StopRequested)
            {
                if (lifetime.TryTakeResize(out var width, out var height))
                {
                    presentation.Resize(width, height);
                }

                var nextFrame = frames + 1;
                if (hasCapture && schedule!.Current == nextFrame)
                {
                    pendingCapture = presentation.CaptureAsync(options.Capture!.PathFor(nextFrame));
                }

                var now = clock.Elapsed;
                presentation.Render(now, now - previous);
                previous = now;
                frames++;
                if (options.FrameLimit is { } limit && frames >= limit)
                {
                    lifetime.RequestStop();
                }

                if (pendingCapture is not null)
                {
                    // A skipped surface acquisition can leave the request queued without a writer.
                    // Bound this wait even before the frame limit starts normal shutdown.
                    pendingCapture.WaitAsync(options.ShutdownTimeout).GetAwaiter().GetResult();
                    pendingCapture = null;
                    captured++;
                    hasCapture = schedule!.MoveNext();
                }
            }
        }
        catch (Exception exception)
        {
            lifetime.Fail(exception);
        }
        finally
        {
            lifetime.RequestStop();
            // Even a failed submission can leave an asynchronous writer holding renderer data.
            // A stuck task intentionally keeps this worker alive so Run can retain native state.
            if (pendingCapture is not null)
            {
                try
                {
                    pendingCapture.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    lifetime.Fail(exception);
                }
            }

            schedule?.Dispose();
            if (captured < (options.Capture?.Frames.Count ?? 0))
            {
                lifetime.Fail(new InvalidOperationException("The application stopped before all requested frames were captured."));
            }

            Dispose(presentation, lifetime);
            lifetime.MarkPresentationStopped();
        }
    }

    private static bool Join(Thread worker, Stopwatch shutdown, TimeSpan timeout)
    {
        var remaining = timeout - shutdown.Elapsed;
        return worker.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    private static void Dispose(IDisposable? resource, HostLifetime lifetime)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            lifetime.Fail(exception);
        }
    }

    [LoggerMessage(1, LogLevel.Error, "Application host failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

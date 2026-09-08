using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Windowing;

namespace Paradise.Hosting.Test;

public sealed class HostTests
{
    [Test]
    public async Task FrameLimitAndCapture_PreserveOwnerThreadsAndDisposalOrder()
    {
        var app = new TestApplication();
        var platform = new TestPlatform();
        var switches = new FeatureSwitches();
        var caller = Environment.CurrentManagedThreadId;
        var options = new HostOptions
        {
            FrameLimit = 3,
            Capture = new CaptureRequest("frame.png", [2, 3], null),
        };

        var result = ParadiseHost.Run(app, platform, options, switches, NullLogger.Instance);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(app.SimulationThread).IsNotEqualTo(caller);
        await Assert.That(app.PresentationThread).IsNotEqualTo(caller);
        await Assert.That(app.SimulationThread).IsNotEqualTo(app.PresentationThread);
        await Assert.That(app.SimulationDisposedThread).IsEqualTo(app.SimulationThread);
        await Assert.That(app.PresentationDisposedThread).IsEqualTo(app.PresentationThread);
        await Assert.That(platform.DisposedThread).IsEqualTo(caller);
        await Assert.That(platform.Window.DisposedThread).IsEqualTo(caller);
        await Assert.That(app.Switches).IsSameReferenceAs(switches);
        await Assert.That(app.Frames).IsEqualTo(3);
        await Assert.That(string.Join(",", app.Events)).IsEqualTo(
            "simulation-created,presentation-created,render:1,capture:frame-00002.png,render:2," +
            "capture:frame-00003.png,render:3,presentation-disposed,simulation-disposed");
    }

    [Test]
    public async Task FailedSimulationStartup_DoesNotCreatePresentation()
    {
        var app = new TestApplication { FailSimulationCreation = true };
        var platform = new TestPlatform();

        var result = ParadiseHost.Run(app, platform, new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(app.PresentationThread).IsEqualTo(0);
        await Assert.That(platform.DisposedThread).IsNotEqualTo(0);
    }

    [Test]
    public async Task FailedPresentationStartup_ReleasesInitializedSimulation()
    {
        var app = new TestApplication { FailPresentationCreation = true };
        var platform = new TestPlatform();

        var result = ParadiseHost.Run(app, platform, new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(app.SimulationDisposedThread).IsEqualTo(app.SimulationThread);
        await Assert.That(platform.DisposedThread).IsNotEqualTo(0);
    }

    [Test]
    public async Task FailedFrame_StopsWorkersAndReleasesPresentationBeforeSimulation()
    {
        var app = new TestApplication { FailRender = true };
        var platform = new TestPlatform();

        var result = ParadiseHost.Run(app, platform, new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(string.Join(",", app.Events)).IsEqualTo(
            "simulation-created,presentation-created,render:1,presentation-disposed,simulation-disposed");
        await Assert.That(platform.DisposedThread).IsNotEqualTo(0);
    }

    [Test]
    public async Task CaptureStillWritingAtShutdown_RetainsBorrowedResources()
    {
        var app = new TestApplication { FinishCaptureOnRender = false };
        var platform = new TestPlatform();
        var options = new HostOptions
        {
            FrameLimit = 1,
            Capture = new CaptureRequest("frame.png", [1], null),
            ShutdownTimeout = TimeSpan.FromMilliseconds(50),
        };

        try
        {
            var result = ParadiseHost.Run(app, platform, options, new FeatureSwitches(), NullLogger.Instance);

            await Assert.That(result).IsEqualTo(1);
            await Assert.That(app.SimulationDisposedThread).IsEqualTo(0);
            await Assert.That(app.PresentationDisposedThread).IsEqualTo(0);
            await Assert.That(platform.Window.DisposedThread).IsEqualTo(0);
            await Assert.That(platform.DisposedThread).IsEqualTo(0);
        }
        finally
        {
            app.CompleteCapture();
            if (!SpinWait.SpinUntil(() => app.SimulationDisposedThread != 0, TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The capture writer did not release simulation resources.");
            }

            platform.Window.Dispose();
            platform.Dispose();
        }
    }

    [Test]
    public async Task CaptureStallBeforeFrameLimit_StopsHostAndRetainsBorrowedResources()
    {
        var app = new TestApplication { FinishCaptureOnRender = false };
        var platform = new TestPlatform();
        var options = new HostOptions
        {
            FrameLimit = 3,
            Capture = new CaptureRequest("frame.png", [1], null),
            ShutdownTimeout = TimeSpan.FromMilliseconds(50),
        };
        var run = Task.Run(() => ParadiseHost.Run(app, platform, options, new FeatureSwitches(), NullLogger.Instance));

        try
        {
            var result = await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            await Assert.That(result).IsEqualTo(1);
            await Assert.That(app.Frames).IsEqualTo(1);
            await Assert.That(app.SimulationDisposedThread).IsEqualTo(0);
            await Assert.That(app.PresentationDisposedThread).IsEqualTo(0);
            await Assert.That(platform.Window.DisposedThread).IsEqualTo(0);
            await Assert.That(platform.DisposedThread).IsEqualTo(0);
        }
        finally
        {
            // Release the fake writer even if the host regresses to an unbounded wait.
            app.CompleteCapture();
            await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!SpinWait.SpinUntil(() => app.SimulationDisposedThread != 0, TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The capture writer did not release simulation resources.");
            }

            platform.Window.Dispose();
            platform.Dispose();
        }
    }

    [Test]
    public async Task UnreachableCapture_IsRejectedBeforeStartup()
    {
        var app = new TestApplication();
        var platform = new TestPlatform();
        var options = new HostOptions { FrameLimit = 2, Capture = new CaptureRequest("frame.png", [3], null) };

        var result = ParadiseHost.Run(app, platform, options, new FeatureSwitches(), NullLogger.Instance);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(app.SimulationThread).IsEqualTo(0);
        await Assert.That(platform.DisposedThread).IsNotEqualTo(0);
    }

    [Test]
    public async Task FixedStepClock_PreservesRemainderAndBoundsLongStalls()
    {
        var clock = new FixedStepClock(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(250));

        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(9))).IsEqualTo(0);
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(24))).IsEqualTo(3);
        await Assert.That(clock.Advance(TimeSpan.FromSeconds(5))).IsEqualTo(25);
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(7))).IsEqualTo(1);
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(-50))).IsEqualTo(0);
    }

    private sealed class TestApplication : IHostApplication, IHostSimulation, IHostPresentation
    {
        private readonly TaskCompletionSource _capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _simulationDisposedThread;
        private int _presentationDisposedThread;

        public ConcurrentQueue<string> Events { get; } = new();
        public FeatureSwitches? Switches { get; private set; }
        public int SimulationThread { get; private set; }
        public int PresentationThread { get; private set; }
        public int SimulationDisposedThread => Volatile.Read(ref _simulationDisposedThread);
        public int PresentationDisposedThread => Volatile.Read(ref _presentationDisposedThread);
        public int Frames { get; private set; }
        public bool FailSimulationCreation { get; init; }
        public bool FailPresentationCreation { get; init; }
        public bool FailRender { get; init; }
        public bool FinishCaptureOnRender { get; init; } = true;
        public TimeSpan FixedStep => TimeSpan.FromMilliseconds(1);

        public IHostSimulation CreateSimulation(HostContext context)
        {
            if (FailSimulationCreation) throw new InvalidOperationException("Simulation startup failed.");
            SimulationThread = Environment.CurrentManagedThreadId;
            Switches = context.Switches;
            Events.Enqueue("simulation-created");
            return this;
        }

        public IHostPresentation CreatePresentation(HostContext context, in SurfaceDescriptor surface)
        {
            if (SimulationThread == 0) throw new InvalidOperationException("Simulation was not initialized first.");
            if (!ReferenceEquals(Switches, context.Switches)) throw new InvalidOperationException("Switches must be shared.");
            if (FailPresentationCreation) throw new InvalidOperationException("Presentation startup failed.");
            PresentationThread = Environment.CurrentManagedThreadId;
            Events.Enqueue("presentation-created");
            return this;
        }

        public void HandleInput(in TimedWindowEvent input) { }
        public void Tick() { }
        public void AfterTicks() { }
        public void Resize(uint width, uint height) { }

        public void Render(TimeSpan elapsed, TimeSpan delta)
        {
            Frames++;
            Events.Enqueue($"render:{Frames}");
            if (FailRender) throw new InvalidOperationException("Render failed.");
            if (FinishCaptureOnRender) _capture.TrySetResult();
        }

        public Task CaptureAsync(string path)
        {
            Events.Enqueue($"capture:{path}");
            return _capture.Task;
        }

        public void CompleteCapture() => _capture.TrySetResult();

        void IDisposable.Dispose()
        {
            var current = Environment.CurrentManagedThreadId;
            if (current == PresentationThread)
            {
                if (SimulationDisposedThread != 0) throw new InvalidOperationException("World disposed before renderer.");
                Events.Enqueue("presentation-disposed");
                Volatile.Write(ref _presentationDisposedThread, current);
            }
            else
            {
                if (PresentationThread != 0 && PresentationDisposedThread == 0)
                    throw new InvalidOperationException("World disposed before renderer.");
                Events.Enqueue("simulation-disposed");
                Volatile.Write(ref _simulationDisposedThread, current);
            }
        }
    }

    private sealed class TestPlatform : IWindowPlatform
    {
        public TestWindow Window { get; } = new();
        public int DisposedThread { get; private set; }
        public IWindow CreateWindow(in WindowOptions options) => Window;
        public void Pump() { }
        public void Dispose() => DisposedThread = Environment.CurrentManagedThreadId;
    }

    private sealed class TestWindow : IWindow
    {
        public uint Width => 1;
        public uint Height => 1;
        public bool CloseRequested => false;
        public int DisposedThread { get; private set; }
        public event Action<uint, uint>? Resized { add { } remove { } }
        public void RequestClose() { }
        public bool TryReadEvent(out TimedWindowEvent input)
        {
            input = default;
            return false;
        }

        public SurfaceDescriptor CreateSurface() => SurfaceDescriptor.Headless();
        public void Dispose() => DisposedThread = Environment.CurrentManagedThreadId;
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Windowing;

namespace Paradise.Hosting.Test;

public sealed class CooperativeHostTests
{
    [Test]
    public async Task frames_run_on_the_caller_with_captures_and_release_in_order()
    {
        var app = new App();
        var platform = new Platform();
        var options = new HostOptions { FrameLimit = 3, Capture = new CaptureRequest("frame.png", [2, 3], null) };
        var caller = Environment.CurrentManagedThreadId;

        var host = CooperativeHost.Start(app, platform, options, new FeatureSwitches(), NullLogger.Instance);
        var frames = 0;
        while (host.Frame() && frames++ < 100) { }
        host.Dispose();

        await Assert.That(host.Failure).IsNull();
        await Assert.That(host.Frames).IsEqualTo(3);
        await Assert.That(app.Threads.Distinct().ToArray()).IsEquivalentTo([caller]);
        await Assert.That(string.Join(",", app.Events)).IsEqualTo(
            "simulation-created,presentation-created,render:1,capture:frame-00002.png,render:2," +
            "capture:frame-00003.png,render:3,presentation-disposed,simulation-disposed");
        await Assert.That(platform.Window.Disposed).IsTrue();
        await Assert.That(platform.Disposed).IsTrue();
    }

    [Test]
    public async Task simulation_ticks_and_render_times_share_the_host_clock()
    {
        var app = new App();
        using var host = CooperativeHost.Start(app, new Platform(), new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(15);
            host.Frame();
        }

        await Assert.That(app.Ticks).IsGreaterThan(0);
        await Assert.That(app.Context!.Clock).IsSameReferenceAs(host.Context.Clock);
        // Render's elapsed time is the clock's, not a presentation-local stopwatch started later.
        await Assert.That(app.RenderTimes[0]).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(15));
        await Assert.That(app.RenderTimes[^1]).IsLessThanOrEqualTo(host.Context.Clock.Elapsed);
    }

    [Test]
    public async Task a_failing_render_stops_the_run_and_still_releases_in_order()
    {
        var app = new App { FailRender = true };
        var host = CooperativeHost.Start(app, new Platform(), new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        var running = host.Frame();
        host.Dispose();

        await Assert.That(running).IsFalse();
        await Assert.That(host.Failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(string.Join(",", app.Events)).IsEqualTo(
            "simulation-created,presentation-created,render:1,presentation-disposed,simulation-disposed");
    }

    [Test]
    public async Task a_failed_start_releases_what_it_created_and_leaves_the_platform_to_the_caller()
    {
        var app = new App { FailPresentationCreation = true };
        var platform = new Platform();

        await Assert.That(() => CooperativeHost.Start(app, platform, new HostOptions(), new FeatureSwitches(), NullLogger.Instance))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(string.Join(",", app.Events)).IsEqualTo("simulation-created,simulation-disposed");
        await Assert.That(platform.Window.Disposed).IsTrue();
        await Assert.That(platform.Disposed).IsFalse();
    }

    [Test]
    public async Task a_pending_capture_holds_the_run_open_without_blocking_a_frame()
    {
        var app = new App { FinishCaptureOnRender = false };
        var options = new HostOptions { FrameLimit = 1, Capture = new CaptureRequest("frame.png", [1], null) };
        var host = CooperativeHost.Start(app, new Platform(), options, new FeatureSwitches(), NullLogger.Instance);

        await Assert.That(host.Frame()).IsTrue();
        await Assert.That(host.Frame()).IsTrue();
        await Assert.That(host.Dispose).ThrowsExactly<InvalidOperationException>();

        app.CompleteCapture();
        await Assert.That(host.Frame()).IsFalse();
        host.Dispose();

        await Assert.That(host.Failure).IsNull();
        await Assert.That(app.Frames).IsEqualTo(1);
        await Assert.That(app.Events).Contains("simulation-disposed");
    }

    [Test]
    public async Task an_overdue_capture_ends_the_run_and_retains_what_it_may_read()
    {
        var app = new App { FinishCaptureOnRender = false };
        var options = new HostOptions
        {
            FrameLimit = 1,
            Capture = new CaptureRequest("frame.png", [1], null),
            ShutdownTimeout = TimeSpan.FromMilliseconds(30),
        };
        var host = CooperativeHost.Start(app, new Platform(), options, new FeatureSwitches(), NullLogger.Instance);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (host.Frame() && DateTime.UtcNow < deadline) Thread.Sleep(5);
        host.Dispose();

        await Assert.That(host.IsRunning).IsFalse();
        await Assert.That(host.Failure).IsTypeOf<TimeoutException>();
        await Assert.That(app.Events).DoesNotContain("presentation-disposed");
        await Assert.That(app.Events).DoesNotContain("simulation-disposed");
        app.CompleteCapture();
    }

    [Test]
    public async Task closing_the_window_stops_the_run_and_resizes_reach_presentation_first()
    {
        var app = new App();
        var platform = new Platform();
        var host = CooperativeHost.Start(app, platform, new HostOptions(), new FeatureSwitches(), NullLogger.Instance);

        platform.Window.Resize(640, 360);
        await Assert.That(host.Frame()).IsTrue();
        platform.Window.RequestClose();
        await Assert.That(host.Frame()).IsFalse();
        host.Dispose();

        await Assert.That(app.Events.ToArray()[2]).IsEqualTo("resize:640x360");
        await Assert.That(app.Frames).IsEqualTo(1);
        await Assert.That(host.Failure).IsNull();
    }

    [Test]
    public async Task snapshots_carry_the_time_they_were_published_at()
    {
        var stream = new SnapshotStream<object>();
        var world = new object();
        stream.Publish(world, frame: 7, time: TimeSpan.FromMilliseconds(250));
        stream.Publish(new object(), frame: 8);

        await Assert.That(stream.TryRead(out var stamped)).IsTrue();
        await Assert.That(stamped.Frame).IsEqualTo(7L);
        await Assert.That(stamped.Time).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(stream.TryRead(out var unstamped)).IsTrue();
        await Assert.That(unstamped.Time).IsEqualTo(TimeSpan.Zero);
    }

    private sealed class App : IHostApplication, IHostSimulation, IHostPresentation
    {
        private readonly TaskCompletionSource _capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _presentationCreated;

        public ConcurrentQueue<string> Events { get; } = new();
        public List<int> Threads { get; } = [];
        public List<TimeSpan> RenderTimes { get; } = [];
        public HostContext? Context { get; private set; }
        public int Frames { get; private set; }
        public int Ticks { get; private set; }
        public bool FailPresentationCreation { get; init; }
        public bool FailRender { get; init; }
        public bool FinishCaptureOnRender { get; init; } = true;
        public TimeSpan FixedStep => TimeSpan.FromMilliseconds(1);

        public IHostSimulation CreateSimulation(HostContext context)
        {
            Context = context;
            Record("simulation-created");
            return this;
        }

        public IHostPresentation CreatePresentation(HostContext context, in SurfaceDescriptor surface)
        {
            if (FailPresentationCreation) throw new InvalidOperationException("Presentation startup failed.");
            _presentationCreated = true;
            Record("presentation-created");
            return new PresentationView(this);
        }

        public void HandleInput(in TimedWindowEvent input) { }
        public void Tick() => Ticks++;
        public void AfterTicks() { }

        public void Resize(uint width, uint height) => Record($"resize:{width}x{height}");

        public void Render(TimeSpan elapsed, TimeSpan delta)
        {
            Frames++;
            RenderTimes.Add(elapsed);
            Record($"render:{Frames}");
            if (FailRender) throw new InvalidOperationException("Render failed.");
            if (FinishCaptureOnRender) _capture.TrySetResult();
        }

        public Task CaptureAsync(string path)
        {
            Record($"capture:{path}");
            return _capture.Task;
        }

        public void CompleteCapture() => _capture.TrySetResult();

        // The simulation's Dispose; presentation disposes through PresentationView.
        void IDisposable.Dispose()
        {
            if (_presentationCreated && !Events.Contains("presentation-disposed"))
                throw new InvalidOperationException("World disposed before renderer.");
            Record("simulation-disposed");
        }

        internal void DisposePresentation() => Record("presentation-disposed");

        private void Record(string entry)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Events.Enqueue(entry);
        }
    }

    private sealed class PresentationView(App app) : IHostPresentation
    {
        public void Resize(uint width, uint height) => app.Resize(width, height);
        public void Render(TimeSpan elapsed, TimeSpan delta) => app.Render(elapsed, delta);
        public Task CaptureAsync(string path) => app.CaptureAsync(path);
        public void Dispose() => app.DisposePresentation();
    }

    private sealed class Platform : IWindowPlatform
    {
        public Window Window { get; } = new();
        public bool Disposed { get; private set; }
        public IWindow CreateWindow(in WindowOptions options) => Window;
        public void Pump() => Window.DeliverResize();
        public void Dispose() => Disposed = true;
    }

    private sealed class Window : IWindow
    {
        private (uint Width, uint Height)? _pendingResize;

        public uint Width { get; private set; } = 1;
        public uint Height { get; private set; } = 1;
        public bool CloseRequested { get; private set; }
        public bool Disposed { get; private set; }
        public event Action<uint, uint>? Resized;

        public void Resize(uint width, uint height) => _pendingResize = (width, height);

        // Like a real platform, resizes are raised from Pump.
        public void DeliverResize()
        {
            if (_pendingResize is not { } size) return;
            _pendingResize = null;
            (Width, Height) = size;
            Resized?.Invoke(size.Width, size.Height);
        }

        public void RequestClose() => CloseRequested = true;

        public bool TryReadEvent(out TimedWindowEvent input)
        {
            input = default;
            return false;
        }

        public SurfaceDescriptor CreateSurface() => SurfaceDescriptor.Headless();
        public void Dispose() => Disposed = true;
    }
}

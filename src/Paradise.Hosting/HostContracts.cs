using Paradise.Features;
using Paradise.Windowing;

namespace Paradise.Hosting;

/// <summary>Provides the shared window, configuration and initial pixel size to application components.</summary>
/// <remarks>Size is captured on the main thread before workers start. Subsequent resizes travel
/// through ordered input events and the presentation callback.</remarks>
public sealed record HostContext(IWindow Window, FeatureSwitches Switches, uint Width, uint Height);

/// <summary>Creates simulation and presentation on their owning threads.</summary>
public interface IHostApplication
{
    IHostSimulation CreateSimulation(HostContext context);
    IHostPresentation CreatePresentation(HostContext context, in SurfaceDescriptor surface);
}

/// <summary>Supplies game behavior to the host's fixed-step loop.</summary>
/// <remarks>All methods, including creation and disposal, run on the simulation thread.
/// Disposal occurs only after presentation has stopped using simulation resources.</remarks>
public interface IHostSimulation : IDisposable
{
    TimeSpan FixedStep { get; }
    void HandleInput(in TimedWindowEvent input);
    void Tick();
    void AfterTicks();
}

/// <summary>Supplies one frame of game presentation to the host.</summary>
/// <remarks>All calls run on the render thread. CaptureAsync queues capture of the next
/// Render call; its task includes writing the result and must complete before disposal.</remarks>
public interface IHostPresentation : IDisposable
{
    void Resize(uint width, uint height);
    void Render(TimeSpan elapsed, TimeSpan delta);
    Task CaptureAsync(string path);
}

/// <summary>Configures the window and execution limits for an application.</summary>
public sealed record HostOptions
{
    public WindowOptions Window { get; init; } = new("Paradise", 1280, 720);
    public int? FrameLimit { get; init; }
    public TimeSpan MaxCatchUp { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Bounds each capture wait and the final worker join phase.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public CaptureRequest? Capture { get; init; }
    public bool Headless { get; init; }
    public IReadOnlyList<KeyboardKey> Hold { get; init; } = [];
}

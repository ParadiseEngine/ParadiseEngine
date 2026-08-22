namespace Paradise.Windowing;

/// <summary>What a window is created from. Width and height are in PIXELS (the surface size a
/// renderer wants), not desktop points.</summary>
public readonly record struct WindowOptions(string Title, uint Width, uint Height)
{
    public bool Resizable { get; init; } = true;
}

/// <summary>
/// The windowing backend: owns the platform's global state (SDL's init/quit, an OS event
/// hook), creates windows, and PUMPS them. Create it, create windows from it, dispose windows
/// before it.
///
/// The pump lives here, not on the window, because the OS event queue is per-process (or
/// per-thread), not per-window: one drain routes each event to the window it names. Two
/// windows each draining a shared queue would eat each other's events — a structural
/// impossibility, not a filtering bug.
///
/// THREAD CONTRACT: construct, create windows, and pump on the MAIN thread — macOS delivers
/// window events nowhere else, and every backend inherits that as its portable rule.
/// </summary>
public interface IWindowPlatform : IDisposable
{
    /// <summary>Create a window. Any number may exist; events are routed per window.</summary>
    IWindow CreateWindow(in WindowOptions options);

    /// <summary>Drain the OS event queue and route: input events are timestamped and queued
    /// for the addressed window's <see cref="IWindow.TryReadInput"/>, resizes update its size
    /// and raise <see cref="IWindow.Resized"/>, a close request latches its
    /// <see cref="IWindow.CloseRequested"/>. MAIN THREAD, every frame.</summary>
    void Pump();
}

/// <summary>
/// One OS window, as a game host consumes it: read the raw input stream from any thread, hand
/// a renderer its surface. Events arrive through <see cref="IWindowPlatform.Pump"/> — the
/// queue is the platform's, and it routes.
///
/// Input is TRANSPORT, not meaning: the window reports timestamped device transitions
/// (<see cref="TimedRawInput"/>) and never learns what a key does — bindings, held state and
/// chords belong to the consumer's input layer. <see cref="TryReadInput"/> and
/// <see cref="RequestClose"/> are thread-safe; everything else is main-thread.
/// </summary>
public interface IWindow : IDisposable
{
    /// <summary>Current size in pixels. Tracks live resizes; see <see cref="Resized"/>.</summary>
    uint Width { get; }

    /// <summary>Current size in pixels. Tracks live resizes; see <see cref="Resized"/>.</summary>
    uint Height { get; }

    /// <summary>True once the user closed the window or a consumer called
    /// <see cref="RequestClose"/>. Never resets — a window closes once.</summary>
    bool CloseRequested { get; }

    /// <summary>Raised from <see cref="IWindowPlatform.Pump"/> when the pixel size changed —
    /// where a host resizes its renderer.</summary>
    event Action<uint, uint>? Resized;

    /// <summary>Ask the window to close — the same latch the user's close button sets.
    /// Thread-safe: a sim thread deciding "ESC quits" calls this.</summary>
    void RequestClose();

    /// <summary>Dequeue one raw device transition, oldest first. Thread-safe — this is the
    /// one-way stream a sim thread drains at its own pace.</summary>
    bool TryReadInput(out TimedRawInput input);

    /// <summary>The window's render surface, for a GPU renderer. Call once, on the main
    /// thread, and dispose the renderer BEFORE the window — the surface's native resources
    /// (a CAMetalLayer on macOS) live and die with the window.</summary>
    SurfaceDescriptor CreateSurface();
}

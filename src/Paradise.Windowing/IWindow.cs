using Paradise.Rendering;

namespace Paradise.Windowing;

/// <summary>What a window is created from. Width and height are in PIXELS (the surface size a
/// renderer wants), not desktop points.</summary>
public readonly record struct WindowOptions(string Title, uint Width, uint Height)
{
    public bool Resizable { get; init; } = true;
}

/// <summary>
/// The windowing backend: owns the platform's global state (SDL's init/quit, an OS event
/// hook) and creates windows. Create it, create windows from it, dispose windows before it.
///
/// THREAD CONTRACT: construct, create windows, and pump on the MAIN thread — macOS delivers
/// window events nowhere else, and every backend inherits that as its portable rule.
/// </summary>
public interface IWindowPlatform : IDisposable
{
    IWindow CreateWindow(in WindowOptions options);
}

/// <summary>
/// One OS window, as a game host consumes it: pump events on the main thread, read the raw
/// input stream from any thread, hand a renderer its surface.
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

    /// <summary>Raised from <see cref="Pump"/> when the pixel size changed — where a host
    /// resizes its renderer.</summary>
    event Action<uint, uint>? Resized;

    /// <summary>Drain the OS event queue: input events are timestamped and queued for
    /// <see cref="TryReadInput"/>, resizes update the size and raise <see cref="Resized"/>,
    /// a close request latches <see cref="CloseRequested"/>. MAIN THREAD, every frame.</summary>
    void Pump();

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

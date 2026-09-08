namespace Paradise.Windowing;

/// <summary>What a window is created from. Width and height are in PIXELS (the surface size a
/// renderer wants), not desktop points.</summary>
public readonly record struct WindowOptions(string Title, uint Width, uint Height)
{
    public bool Resizable { get; init; } = true;
}

/// <summary>Owns platform state, window creation and event pumping.</summary>
/// <remarks>Construct, create windows and pump on the main thread; dispose windows before the
/// platform. A single drain routes the shared OS queue to each window.</remarks>
public interface IWindowPlatform : IDisposable
{
    /// <summary>Create a window. Any number may exist; events are routed per window.</summary>
    IWindow CreateWindow(in WindowOptions options);

    /// <summary>Drain the OS event queue and route: input events are timestamped and queued
    /// for the addressed window's <see cref="IWindow.TryReadEvent"/>, resizes update its size
    /// and raise <see cref="IWindow.Resized"/>, a close request latches its
    /// <see cref="IWindow.CloseRequested"/>. MAIN THREAD, every frame.</summary>
    void Pump();
}

/// <summary>Exposes one window's surface and timestamped input transitions.</summary>
/// <remarks>IWindowPlatform.Pump routes events here. TryReadEvent and RequestClose are thread-safe;
/// all other operations require the main thread. Bindings and held input state belong to the
/// consumer.</remarks>
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
    bool TryReadEvent(out TimedWindowEvent input);

    /// <summary>The window's render surface, for a GPU renderer. Call once, on the main
    /// thread, and dispose the renderer BEFORE the window — the surface's native resources
    /// (a CAMetalLayer on macOS) live and die with the window.</summary>
    SurfaceDescriptor CreateSurface();
}

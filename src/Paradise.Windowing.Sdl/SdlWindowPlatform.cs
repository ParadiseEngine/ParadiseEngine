using System.Diagnostics;
using SDL;
using static SDL.SDL3;

namespace Paradise.Windowing.Sdl;

/// <summary>
/// The SDL3 backend's platform half: initializes SDL's video subsystem for its lifetime,
/// creates <see cref="SdlWindow"/>s, and PUMPS them.
///
/// The pump is here because SDL's event queue is per-PROCESS: one drain, routed to the window
/// each event names by its <c>windowID</c>. Two windows each polling that queue would consume
/// each other's events, so routing is the only correct shape — not a filter bolted onto a
/// per-window pump.
///
/// One instance per process; dispose windows before it. Main thread throughout — see the
/// contract on <see cref="IWindowPlatform"/>.
/// </summary>
public sealed unsafe class SdlWindowPlatform : IWindowPlatform
{
    /// <summary>One clock for every window this platform creates, so timestamps from two
    /// windows share an epoch and compare directly — the contract on
    /// <see cref="TimedRawInput"/>.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly Dictionary<uint, SdlWindow> _windows = [];
    private bool _disposed;

    public SdlWindowPlatform()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        }
    }

    public IWindow CreateWindow(in WindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = new SdlWindow(in options, this);
        _windows[window.Id] = window;
        return window;
    }

    public void Pump()
    {
        var now = _clock.Elapsed;
        SDL_Event ev;
        while (SDL_PollEvent(&ev))
        {
            var type = (SDL_EventType)ev.type;
            switch (type)
            {
                // A quit request is the APPLICATION's, not any one window's: SDL raises it for
                // the last window closing, a dock quit, a terminating signal. Every window
                // latches, so a host watching any of them sees it.
                case SDL_EventType.SDL_EVENT_QUIT:
                    foreach (var window in _windows.Values)
                    {
                        window.OnClose();
                    }
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    Route(ev.window.windowID)?.OnClose();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                    Route(ev.window.windowID)?.OnPixelSizeChanged();
                    break;

                // Auto-repeat is filtered: a held key must not read as a stream of presses.
                case SDL_EventType.SDL_EVENT_KEY_DOWN when !ev.key.repeat:
                    Route(ev.key.windowID)?.OnKey(ev.key.scancode, pressed: true, now);
                    break;

                case SDL_EventType.SDL_EVENT_KEY_UP:
                    Route(ev.key.windowID)?.OnKey(ev.key.scancode, pressed: false, now);
                    break;
            }
        }
    }

    /// <summary>The window an event names, or null when it names none this platform owns —
    /// a window already disposed, or SDL's 0 for "no window" (a keystroke with nothing
    /// focused). Dropping those is correct: there is nobody to deliver them to.</summary>
    private SdlWindow? Route(SDL_WindowID id) =>
        _windows.TryGetValue((uint)id, out var window) ? window : null;

    /// <summary>A window drops itself from the routing table when disposed.</summary>
    internal void Forget(uint id) => _windows.Remove(id);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _windows.Clear();
        SDL_Quit();
    }
}

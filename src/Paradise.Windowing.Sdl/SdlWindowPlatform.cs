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

    /// <summary>Open gamepads, joystick instance id → the contract's slot. SDL's instance ids
    /// are opaque and monotonically increasing (unplug and replug the same pad twice and it is
    /// id 1 then 2), so they cannot BE the slot: a game binding "player 1" to a slot needs the
    /// number to be small, stable and reused. The lowest free index is assigned instead.</summary>
    private readonly Dictionary<uint, (byte Slot, IntPtr Handle)> _gamepads = [];

    private bool _disposed;

    public SdlWindowPlatform()
    {
        // GAMEPAD alongside VIDEO: the subsystem is what turns raw joysticks into the mapped,
        // position-named buttons and axes the contract speaks. It is initialized unconditionally
        // rather than on demand because SDL only reports the ADDED events for pads present at
        // startup during init — a lazy init would miss every controller already plugged in.
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_GAMEPAD))
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

                // ---- pointer and text ----------------------------------------------------

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    Route(ev.motion.windowID)?.OnPointerMove(ev.motion.x, ev.motion.y, now);
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                    Route(ev.button.windowID)?.OnPointerButton(
                        ev.button.Button, pressed: true, ev.button.x, ev.button.y, now);
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    Route(ev.button.windowID)?.OnPointerButton(
                        ev.button.Button, pressed: false, ev.button.x, ev.button.y, now);
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    Route(ev.wheel.windowID)?.OnScroll(ev.wheel.x, ev.wheel.y, now);
                    break;

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    Route(ev.text.windowID)?.OnText(ev.text.text, now);
                    break;

                // ---- gamepad -------------------------------------------------------------
                //
                // These name a JOYSTICK, not a window — SDL puts no windowID on them, so the
                // routing every case above uses has nothing to route on. They go to whichever
                // window holds keyboard focus, which is the same answer the OS already gives
                // for typing: an unfocused game stops receiving stick input, and with nothing
                // focused the event is dropped exactly as Route() drops a keystroke for
                // window 0.

                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    OpenGamepad(ev.gdevice.which);
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    CloseGamepad(ev.gdevice.which, now);
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                    if (Slot(ev.gaxis.which) is { } axisSlot)
                    {
                        Focused()?.OnGamepadAxis(
                            (SDL_GamepadAxis)ev.gaxis.axis, ev.gaxis.value, axisSlot, now);
                    }
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                    if (Slot(ev.gbutton.which) is { } buttonSlot)
                    {
                        Focused()?.OnGamepadButton(
                            (SDL_GamepadButton)ev.gbutton.button,
                            type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN,
                            buttonSlot, now);
                    }
                    break;
            }
        }
    }

    /// <summary>The window with keyboard focus, or null — where gamepad input goes.</summary>
    private SdlWindow? Focused()
    {
        var focused = SDL_GetKeyboardFocus();
        return focused == null ? null : Route(SDL_GetWindowID(focused));
    }

    /// <summary>The slot an open gamepad was assigned, or null for one this platform never
    /// opened (a joystick SDL declined to map as a gamepad).</summary>
    private byte? Slot(SDL_JoystickID which) =>
        _gamepads.TryGetValue((uint)which, out var pad) ? pad.Slot : null;

    /// <summary>Open a newly-connected pad and give it the lowest free slot, so unplugging
    /// player 2 and plugging them back in makes them player 2 again.</summary>
    private void OpenGamepad(SDL_JoystickID which)
    {
        if (_gamepads.ContainsKey((uint)which)) return;

        var handle = SDL_OpenGamepad(which);
        if (handle == null)
        {
            Console.Error.WriteLine(
                $"[Paradise.Windowing.Sdl] SDL_OpenGamepad({(uint)which}) failed: {SDL_GetError()}");
            return;
        }

        byte slot = 0;
        while (_gamepads.Values.Any(pad => pad.Slot == slot))
        {
            slot++;
        }
        _gamepads[(uint)which] = (slot, (IntPtr)handle);
    }

    /// <summary>Close a disconnected pad and free its slot. The RELEASES matter as much as the
    /// close: the contract is transitions, so whatever the pad was holding when it vanished
    /// must be let go explicitly or every consumer holds it forever.</summary>
    private void CloseGamepad(SDL_JoystickID which, TimeSpan now)
    {
        if (!_gamepads.Remove((uint)which, out var pad)) return;
        SDL_CloseGamepad((SDL_Gamepad*)pad.Handle);
        foreach (var window in _windows.Values)
        {
            window.OnGamepadRemoved(pad.Slot, now);
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
        foreach (var pad in _gamepads.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)pad.Handle);
        }
        _gamepads.Clear();
        SDL_Quit();
    }
}

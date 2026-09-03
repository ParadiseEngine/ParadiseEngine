using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Paradise.Windowing.Sdl;

/// <summary>
/// One SDL3 window: the state and the surface. Events arrive from
/// <see cref="SdlWindowPlatform.Pump"/>, which owns the process-wide queue and routes each
/// event here by window id; this class turns what it is handed into the contract's vocabulary
/// — timestamped <see cref="WindowEvent"/> transitions (scancodes rather than keycodes, so
/// physical position survives keyboard layouts), resizes, the close latch — and
/// <see cref="CreateSurface"/> maps the native window to a WebGPU-ready
/// <see cref="SurfaceDescriptor"/> per platform.
///
/// Keyboard, pointer, typed text and gamepad (buttons and analog axes). Two conversions happen
/// here and nowhere else, because only this class knows the platform well enough: pointer
/// positions are scaled from SDL's window POINTS into the contract's PIXELS (see
/// <see cref="_pixelDensity"/>), and an analog trigger is reported BOTH as its axis and, across
/// a threshold, as the <see cref="GamepadButton"/> the contract promises.
///
/// <see cref="Handle"/> exposes the native window for consumers that reference this backend
/// DIRECTLY and want more than the contract — a debug overlay renderer, an OS-specific
/// tweak. Code that stays on <see cref="IWindow"/> stays backend-portable.
/// </summary>
public sealed unsafe partial class SdlWindow : IWindow
{
    private readonly SDL_Window* _window;
    private readonly SdlWindowPlatform _platform;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<TimedWindowEvent> _events = new();

    private IntPtr _metalView;
    private volatile bool _closeRequested;
    private bool _disposed;

    /// <summary>Pixels per point, tracked alongside the pixel size. SDL reports pointer
    /// positions in window POINTS while <see cref="Width"/>/<see cref="Height"/> and the
    /// surface are in pixels; on a Retina display those differ by 2, and forwarding SDL's
    /// numbers unscaled puts every click at half its true position — which looks like a
    /// hit-testing bug in whatever consumes it, several layers away from the cause.</summary>
    private float _pixelDensity = 1f;

    /// <summary>Which triggers are currently past <see cref="TriggerPressThreshold"/>, one bit
    /// per slot pair — the state behind the digital half of a trigger (see
    /// <see cref="OnGamepadAxis"/>).</summary>
    private readonly Dictionary<(byte Slot, GamepadAxis Axis), bool> _triggerHeld = [];

    /// <summary>SDL finger id → contract slot, so a two-finger gesture is slots 0 and 1 rather
    /// than two opaque 64-bit handles a consumer would have to intern itself.</summary>
    private readonly Dictionary<ulong, byte> _fingers = [];

    /// <summary>Where an analog trigger starts reading as a pressed BUTTON, and where it stops.
    /// Two values, not one: a trigger resting near the threshold would otherwise chatter out
    /// hundreds of press/release pairs a second, and the contract promises transitions.</summary>
    private const float TriggerPressThreshold = 0.6f;
    private const float TriggerReleaseThreshold = 0.4f;

    internal SdlWindow(in WindowOptions options, SdlWindowPlatform platform, ILogger? logger = null)
    {
        _platform = platform;
        _log = logger ?? NullLogger.Instance;
        var flags = options.Resizable ? SDL_WindowFlags.SDL_WINDOW_RESIZABLE : 0;
        _window = SDL_CreateWindow(options.Title, (int)options.Width, (int)options.Height, flags);
        if (_window == null)
        {
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        }

        Id = (uint)SDL_GetWindowID(_window);
        if (Id == 0)
        {
            SDL_DestroyWindow(_window);
            throw new InvalidOperationException($"SDL_GetWindowID failed: {SDL_GetError()}");
        }
        ReadSizeInPixels();

        // SDL3 delivers no SDL_EVENT_TEXT_INPUT until asked, so a window that never calls this
        // simply never sees typed text — silently, which is the hard way to discover it. On
        // desktop it costs nothing; the reason SDL makes it opt-in is mobile, where it is what
        // raises the on-screen keyboard.
        if (!SDL_StartTextInput(_window))
        {
            LogTextInputUnavailable(_log, SDL_GetError());
        }
    }

    /// <summary>The native SDL window, for consumers that opt into this backend directly.</summary>
    public SDL_Window* Handle => _window;

    /// <summary>SDL's id for this window — how <see cref="SdlWindowPlatform.Pump"/> routes.</summary>
    internal uint Id { get; }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public bool CloseRequested => _closeRequested;

    public event Action<uint, uint>? Resized;

    public void RequestClose() => _closeRequested = true;

    // ---- routed from SdlWindowPlatform.Pump, main thread ---------------------------------

    internal void OnClose() => _closeRequested = true;

    internal void OnPixelSizeChanged(TimeSpan now)
    {
        ReadSizeInPixels();
        // BOTH, and they are not redundant. The stream copy is ordered against the pointer
        // events around it, which is what a UI needs to lay out before hit-testing the next
        // click; the event is for consumers that never drain the stream — a renderer rebuilding
        // its swapchain on another thread.
        _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Resize(Width, Height)));
        Resized?.Invoke(Width, Height);
    }

    /// <summary>A finger moved, went down or came up. SDL reports touch positions NORMALIZED to
    /// 0..1 across the window, unlike the mouse — so they are scaled to pixels here, where the
    /// window size is known, rather than leaving every consumer to discover the difference.</summary>
    internal void OnTouch(SDL_FingerID finger, TouchPhase phase, float normalizedX, float normalizedY, TimeSpan now)
    {
        var x = normalizedX * Width;
        var y = normalizedY * Height;
        // SDL's finger id is a 64-bit opaque handle; the contract's slot is a small index, so
        // ids are interned per window in first-touch order and released on the up.
        var slot = SlotFor(finger, phase);
        _events.Enqueue(new TimedWindowEvent(now, phase switch
        {
            TouchPhase.Move => WindowEvent.TouchMove(slot, x, y),
            _ => WindowEvent.Touch(slot, phase == TouchPhase.Down, x, y),
        }));
    }

    private byte SlotFor(SDL_FingerID finger, TouchPhase phase)
    {
        var id = (ulong)finger;
        if (!_fingers.TryGetValue(id, out var slot))
        {
            slot = 0;
            while (_fingers.ContainsValue(slot) && slot < byte.MaxValue) slot++;
            _fingers[id] = slot;
        }
        if (phase == TouchPhase.Up) _fingers.Remove(id);
        return slot;
    }

    internal void OnKey(SDL_Scancode scancode, bool pressed, TimeSpan now)
    {
        if (ToKeyboardKey(scancode) is { } key)
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Keyboard(key, pressed)));
        }
    }

    internal void OnPointerMove(float x, float y, TimeSpan now) =>
        _events.Enqueue(new TimedWindowEvent(now,
            WindowEvent.PointerMove(x * _pixelDensity, y * _pixelDensity)));

    internal void OnPointerButton(SDLButton button, bool pressed, float x, float y, TimeSpan now)
    {
        if (ToPointerButton(button) is { } mapped)
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Mouse(
                mapped, pressed, x * _pixelDensity, y * _pixelDensity)));
        }
    }

    internal void OnScroll(float deltaX, float deltaY, TimeSpan now) =>
        _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Scroll(deltaX, deltaY)));

    /// <summary>SDL hands text composition back as a NUL-terminated UTF-8 buffer, which for an
    /// IME is a whole phrase rather than a keystroke — so this enqueues one event per CODEPOINT
    /// (and per codepoint, not per UTF-16 char: an emoji is one <see cref="WindowEvent.Text"/>,
    /// not a surrogate pair the consumer would have to reassemble).</summary>
    internal void OnText(byte* utf8, TimeSpan now)
    {
        var text = Marshal.PtrToStringUTF8((IntPtr)utf8);
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Text((uint)rune.Value)));
        }
    }

    internal void OnGamepadButton(SDL_GamepadButton button, bool pressed, byte slot, TimeSpan now)
    {
        if (ToGamepadButton(button) is { } mapped)
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Gamepad(mapped, pressed, slot)));
        }
    }

    /// <summary>An axis settled at a new value. SDL reports a signed 16-bit reading, and the
    /// negative end reaches one further than the positive (-32768 vs 32767) — dividing by
    /// 32767 and clamping is what makes a stick pushed fully left report exactly -1 rather
    /// than -1.00003. Triggers rest at 0 and only ever go positive, so the same scale gives
    /// them 0..1 for free.</summary>
    internal void OnGamepadAxis(SDL_GamepadAxis axis, short value, byte slot, TimeSpan now)
    {
        if (ToGamepadAxis(axis) is not { } mapped) return;

        var normalized = Math.Clamp(value / 32767f, -1f, 1f);
        _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Axis(mapped, normalized, slot)));

        // A trigger is the one control the contract names TWICE: GamepadButton declares
        // Left/RightTrigger and says "the digital threshold is the backend's", while SDL only
        // ever reports triggers as axes. So the analog reading above is the truth, and this is
        // the promised digital view of it — emitted alongside, not instead, so a binder can use
        // either without knowing which device produced it.
        if (ToTriggerButton(mapped) is not { } triggerButton) return;
        var key = (slot, mapped);
        var wasHeld = _triggerHeld.GetValueOrDefault(key);
        var held = wasHeld
            ? normalized > TriggerReleaseThreshold
            : normalized >= TriggerPressThreshold;
        if (held != wasHeld)
        {
            _triggerHeld[key] = held;
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Gamepad(triggerButton, held, slot)));
        }
    }

    /// <summary>A gamepad went away mid-input. Everything it was holding has to be let go
    /// explicitly: the contract is transitions, so a consumer that saw the press and never sees
    /// the release holds the action forever — a controller unplugged mid-push would leave the
    /// player walking for the rest of the run. Releasing every button and centring every axis
    /// is cheap and unconditional; a button that was not held reads as a redundant release,
    /// which every refcounting binder already tolerates.</summary>
    internal void OnGamepadRemoved(byte slot, TimeSpan now)
    {
        for (var button = GamepadButton.South; button <= GamepadButton.Guide; button++)
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Gamepad(button, pressed: false, slot)));
        }
        for (var axis = GamepadAxis.LeftX; axis <= GamepadAxis.RightTrigger; axis++)
        {
            _events.Enqueue(new TimedWindowEvent(now, WindowEvent.Axis(axis, 0f, slot)));
            _triggerHeld.Remove((slot, axis));
        }
    }

    public bool TryReadEvent(out TimedWindowEvent input) => _events.TryDequeue(out input);

    public SurfaceDescriptor CreateSurface()
    {
        var props = SDL_GetWindowProperties(_window);

        if (OperatingSystem.IsWindows())
        {
            var hwnd = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
            return new SurfaceDescriptor(SurfacePlatform.Win32, IntPtr.Zero, hwnd, Width, Height);
        }

        if (OperatingSystem.IsMacOS())
        {
            // SDL owns the CAMetalLayer: SDL_Metal_CreateView attaches a Metal-backed view to
            // the window's content view (main thread — SDL3 requires main-thread video on
            // macOS), and SDL_Metal_GetLayer hands back the CAMetalLayer* Dawn's Cocoa surface
            // needs. The view is destroyed with the window, which is why the renderer must be
            // disposed first.
            _metalView = SDL_Metal_CreateView(_window);
            if (_metalView == IntPtr.Zero)
            {
                throw new InvalidOperationException($"SDL_Metal_CreateView failed: {SDL_GetError()}");
            }
            var layer = SDL_Metal_GetLayer(_metalView);
            if (layer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "SDL_Metal_GetLayer returned null — no CAMetalLayer behind the SDL Metal view.");
            }
            return new SurfaceDescriptor(SurfacePlatform.Cocoa, IntPtr.Zero, layer, Width, Height);
        }

        if (OperatingSystem.IsLinux())
        {
            var wlDisplay = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, IntPtr.Zero);
            if (wlDisplay != IntPtr.Zero)
            {
                var wlSurface = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, IntPtr.Zero);
                return new SurfaceDescriptor(SurfacePlatform.Wayland, wlDisplay, wlSurface, Width, Height);
            }

            var x11Display = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, IntPtr.Zero);
            var x11Window = SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
            return new SurfaceDescriptor(SurfacePlatform.Xlib, x11Display, (IntPtr)x11Window, Width, Height);
        }

        throw new PlatformNotSupportedException(
            $"Surface mapping for the current OS ({RuntimeInformation.OSDescription}) is not implemented.");
    }

    /// <summary>Read the pixel size SDL reports, clamped to at least 1×1. A failed query
    /// (only possible on an invalid window) leaves the last known size standing rather than
    /// collapsing the surface to 1×1 — and says so, because silently rendering at the wrong
    /// size is the kind of thing that gets blamed on the renderer.</summary>
    private void ReadSizeInPixels()
    {
        int w = 0, h = 0;
        if (!SDL_GetWindowSizeInPixels(_window, &w, &h))
        {
            LogSizeQueryFailed(_log, SDL_GetError());
            if (Width != 0)
            {
                return;
            }
            w = h = 1;
        }
        Width = (uint)Math.Max(1, w);
        Height = (uint)Math.Max(1, h);

        // Read here rather than per event: it only changes when the window moves between
        // displays, and that always arrives as a pixel-size change. A failed query (0) means
        // "unknown", and 1 is the only safe guess — it leaves coordinates unscaled rather than
        // multiplying them by zero, which would pin every pointer event to the origin.
        var density = SDL_GetWindowPixelDensity(_window);
        _pixelDensity = density > 0f ? density : 1f;
    }

    /// <summary>SDL's mouse button → the contract's. Anything past X2 is dropped: the contract
    /// covers the buttons a game plausibly binds, not every button a mouse can have.</summary>
    private static PointerButton? ToPointerButton(SDLButton button) => (uint)button switch
    {
        SDL_BUTTON_LEFT => PointerButton.Left,
        SDL_BUTTON_RIGHT => PointerButton.Right,
        SDL_BUTTON_MIDDLE => PointerButton.Middle,
        SDL_BUTTON_X1 => PointerButton.X1,
        SDL_BUTTON_X2 => PointerButton.X2,
        _ => null,
    };

    /// <summary>SDL's gamepad button → the contract's, which names buttons by POSITION, so an
    /// Xbox A and a DualShock cross arrive as the same <see cref="GamepadButton.South"/>. The
    /// paddles, touchpad and misc buttons are dropped — same rule as the keyboard's.</summary>
    private static GamepadButton? ToGamepadButton(SDL_GamepadButton button) => button switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => GamepadButton.South,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => GamepadButton.East,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => GamepadButton.West,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => GamepadButton.North,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => GamepadButton.LeftShoulder,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => GamepadButton.RightShoulder,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => GamepadButton.LeftStick,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => GamepadButton.RightStick,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => GamepadButton.DpadUp,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => GamepadButton.DpadDown,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => GamepadButton.DpadLeft,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => GamepadButton.DpadRight,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => GamepadButton.Start,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => GamepadButton.Back,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE => GamepadButton.Guide,
        _ => null,
    };

    /// <summary>SDL's gamepad axis → the contract's.</summary>
    private static GamepadAxis? ToGamepadAxis(SDL_GamepadAxis axis) => axis switch
    {
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX => GamepadAxis.LeftX,
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY => GamepadAxis.LeftY,
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX => GamepadAxis.RightX,
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY => GamepadAxis.RightY,
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER => GamepadAxis.LeftTrigger,
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER => GamepadAxis.RightTrigger,
        _ => null,
    };

    /// <summary>The button an axis doubles as, for the two that do. Null for a stick.</summary>
    private static GamepadButton? ToTriggerButton(GamepadAxis axis) => axis switch
    {
        GamepadAxis.LeftTrigger => GamepadButton.LeftTrigger,
        GamepadAxis.RightTrigger => GamepadButton.RightTrigger,
        _ => null,
    };

    /// <summary>Scancode → contract key, full coverage of <see cref="KeyboardKey"/>. Anything
    /// SDL reports beyond the contract's vocabulary is dropped here, silently — the contract
    /// is a game's plausible bindings, not a HID table.</summary>
    private static KeyboardKey? ToKeyboardKey(SDL_Scancode scancode) => scancode switch
    {
        >= SDL_Scancode.SDL_SCANCODE_A and <= SDL_Scancode.SDL_SCANCODE_Z =>
            KeyboardKey.A + (byte)(scancode - SDL_Scancode.SDL_SCANCODE_A),
        SDL_Scancode.SDL_SCANCODE_0 => KeyboardKey.Digit0,
        >= SDL_Scancode.SDL_SCANCODE_1 and <= SDL_Scancode.SDL_SCANCODE_9 =>
            KeyboardKey.Digit1 + (byte)(scancode - SDL_Scancode.SDL_SCANCODE_1),
        >= SDL_Scancode.SDL_SCANCODE_F1 and <= SDL_Scancode.SDL_SCANCODE_F12 =>
            KeyboardKey.F1 + (byte)(scancode - SDL_Scancode.SDL_SCANCODE_F1),
        SDL_Scancode.SDL_SCANCODE_UP => KeyboardKey.Up,
        SDL_Scancode.SDL_SCANCODE_DOWN => KeyboardKey.Down,
        SDL_Scancode.SDL_SCANCODE_LEFT => KeyboardKey.Left,
        SDL_Scancode.SDL_SCANCODE_RIGHT => KeyboardKey.Right,
        SDL_Scancode.SDL_SCANCODE_SPACE => KeyboardKey.Space,
        SDL_Scancode.SDL_SCANCODE_RETURN => KeyboardKey.Enter,
        SDL_Scancode.SDL_SCANCODE_ESCAPE => KeyboardKey.Escape,
        SDL_Scancode.SDL_SCANCODE_TAB => KeyboardKey.Tab,
        SDL_Scancode.SDL_SCANCODE_BACKSPACE => KeyboardKey.Backspace,
        SDL_Scancode.SDL_SCANCODE_DELETE => KeyboardKey.Delete,
        SDL_Scancode.SDL_SCANCODE_INSERT => KeyboardKey.Insert,
        SDL_Scancode.SDL_SCANCODE_HOME => KeyboardKey.Home,
        SDL_Scancode.SDL_SCANCODE_END => KeyboardKey.End,
        SDL_Scancode.SDL_SCANCODE_PAGEUP => KeyboardKey.PageUp,
        SDL_Scancode.SDL_SCANCODE_PAGEDOWN => KeyboardKey.PageDown,
        SDL_Scancode.SDL_SCANCODE_LSHIFT => KeyboardKey.LeftShift,
        SDL_Scancode.SDL_SCANCODE_RSHIFT => KeyboardKey.RightShift,
        SDL_Scancode.SDL_SCANCODE_LCTRL => KeyboardKey.LeftControl,
        SDL_Scancode.SDL_SCANCODE_RCTRL => KeyboardKey.RightControl,
        SDL_Scancode.SDL_SCANCODE_LALT => KeyboardKey.LeftAlt,
        SDL_Scancode.SDL_SCANCODE_RALT => KeyboardKey.RightAlt,
        SDL_Scancode.SDL_SCANCODE_LGUI => KeyboardKey.LeftMeta,
        SDL_Scancode.SDL_SCANCODE_RGUI => KeyboardKey.RightMeta,
        SDL_Scancode.SDL_SCANCODE_MINUS => KeyboardKey.Minus,
        SDL_Scancode.SDL_SCANCODE_EQUALS => KeyboardKey.Equals,
        SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => KeyboardKey.LeftBracket,
        SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => KeyboardKey.RightBracket,
        SDL_Scancode.SDL_SCANCODE_BACKSLASH => KeyboardKey.Backslash,
        SDL_Scancode.SDL_SCANCODE_SEMICOLON => KeyboardKey.Semicolon,
        SDL_Scancode.SDL_SCANCODE_APOSTROPHE => KeyboardKey.Apostrophe,
        SDL_Scancode.SDL_SCANCODE_GRAVE => KeyboardKey.Grave,
        SDL_Scancode.SDL_SCANCODE_COMMA => KeyboardKey.Comma,
        SDL_Scancode.SDL_SCANCODE_PERIOD => KeyboardKey.Period,
        SDL_Scancode.SDL_SCANCODE_SLASH => KeyboardKey.Slash,
        SDL_Scancode.SDL_SCANCODE_KP_0 => KeyboardKey.Numpad0,
        >= SDL_Scancode.SDL_SCANCODE_KP_1 and <= SDL_Scancode.SDL_SCANCODE_KP_9 =>
            KeyboardKey.Numpad1 + (byte)(scancode - SDL_Scancode.SDL_SCANCODE_KP_1),
        SDL_Scancode.SDL_SCANCODE_KP_DIVIDE => KeyboardKey.NumpadDivide,
        SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY => KeyboardKey.NumpadMultiply,
        SDL_Scancode.SDL_SCANCODE_KP_MINUS => KeyboardKey.NumpadMinus,
        SDL_Scancode.SDL_SCANCODE_KP_PLUS => KeyboardKey.NumpadPlus,
        SDL_Scancode.SDL_SCANCODE_KP_ENTER => KeyboardKey.NumpadEnter,
        SDL_Scancode.SDL_SCANCODE_KP_PERIOD => KeyboardKey.NumpadPeriod,
        SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR => KeyboardKey.NumLock,
        SDL_Scancode.SDL_SCANCODE_CAPSLOCK => KeyboardKey.CapsLock,
        SDL_Scancode.SDL_SCANCODE_PRINTSCREEN => KeyboardKey.PrintScreen,
        SDL_Scancode.SDL_SCANCODE_SCROLLLOCK => KeyboardKey.ScrollLock,
        SDL_Scancode.SDL_SCANCODE_PAUSE => KeyboardKey.Pause,
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _closeRequested = true;
        _platform.Forget(Id);

        // The Metal view (and its CAMetalLayer) must outlive the renderer's surface — the
        // ordering contract on IWindow.CreateSurface.
        if (_metalView != IntPtr.Zero)
        {
            SDL_Metal_DestroyView(_metalView);
        }
        SDL_DestroyWindow(_window);
    }

    /// <remarks>Warning rather than Error because the window still works — it just never reports
    /// typed text, which is the hard failure to discover by looking at it.</remarks>
    [LoggerMessage(EventId = 70, Level = LogLevel.Warning, Message = "SDL_StartTextInput failed: {Error}; typed text will not be reported.")]
    private static partial void LogTextInputUnavailable(ILogger logger, string? error);

    [LoggerMessage(EventId = 71, Level = LogLevel.Warning, Message = "SDL_GetWindowSizeInPixels failed: {Error}")]
    private static partial void LogSizeQueryFailed(ILogger logger, string? error);
}

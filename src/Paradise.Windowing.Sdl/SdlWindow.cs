using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Paradise.Rendering;
using SDL;
using static SDL.SDL3;

namespace Paradise.Windowing.Sdl;

/// <summary>
/// One SDL3 window. The pump converts SDL events into the contract's vocabulary — timestamped
/// <see cref="RawInput"/> transitions (auto-repeat filtered, scancodes rather than keycodes so
/// physical position survives keyboard layouts), resizes, the close latch — and
/// <see cref="CreateSurface"/> maps the native window to a WebGPU-ready
/// <see cref="SurfaceDescriptor"/> per platform.
///
/// Keyboard only, today: the <see cref="GamepadButton"/> vocabulary is declared in the
/// contract, and this backend grows SDL's gamepad subsystem the day a consumer needs it.
///
/// <see cref="Handle"/> exposes the native window for consumers that reference this backend
/// DIRECTLY and want more than the contract — a debug overlay renderer, an OS-specific
/// tweak. Code that stays on <see cref="IWindow"/> stays backend-portable.
/// </summary>
public sealed unsafe class SdlWindow : IWindow
{
    private readonly SDL_Window* _window;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<TimedRawInput> _inputs = new();

    private IntPtr _metalView;
    private volatile bool _closeRequested;
    private bool _disposed;

    internal SdlWindow(in WindowOptions options)
    {
        var flags = options.Resizable ? SDL_WindowFlags.SDL_WINDOW_RESIZABLE : 0;
        _window = SDL_CreateWindow(options.Title, (int)options.Width, (int)options.Height, flags);
        if (_window == null)
        {
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        }

        int w = 0, h = 0;
        SDL_GetWindowSizeInPixels(_window, &w, &h);
        Width = (uint)Math.Max(1, w);
        Height = (uint)Math.Max(1, h);
    }

    /// <summary>The native SDL window, for consumers that opt into this backend directly.</summary>
    public SDL_Window* Handle => _window;

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public bool CloseRequested => _closeRequested;

    public event Action<uint, uint>? Resized;

    public void Pump()
    {
        var now = _clock.Elapsed;
        SDL_Event ev;
        while (SDL_PollEvent(&ev))
        {
            var type = (SDL_EventType)ev.type;
            if (type is SDL_EventType.SDL_EVENT_QUIT
                or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
            {
                _closeRequested = true;
            }
            else if (type is SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED)
            {
                int w = 0, h = 0;
                SDL_GetWindowSizeInPixels(_window, &w, &h);
                Width = (uint)Math.Max(1, w);
                Height = (uint)Math.Max(1, h);
                Resized?.Invoke(Width, Height);
            }
            else if (type is SDL_EventType.SDL_EVENT_KEY_DOWN && !ev.key.repeat)
            {
                Enqueue(ev.key.scancode, pressed: true, now);
            }
            else if (type is SDL_EventType.SDL_EVENT_KEY_UP)
            {
                Enqueue(ev.key.scancode, pressed: false, now);
            }
        }
    }

    public void RequestClose() => _closeRequested = true;

    public bool TryReadInput(out TimedRawInput input) => _inputs.TryDequeue(out input);

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

    private void Enqueue(SDL_Scancode scancode, bool pressed, TimeSpan now)
    {
        if (ToKeyboardKey(scancode) is { } key)
        {
            _inputs.Enqueue(new TimedRawInput(now, RawInput.Keyboard(key, pressed)));
        }
    }

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

        // The Metal view (and its CAMetalLayer) must outlive the renderer's surface — the
        // ordering contract on IWindow.CreateSurface.
        if (_metalView != IntPtr.Zero)
        {
            SDL_Metal_DestroyView(_metalView);
        }
        SDL_DestroyWindow(_window);
    }
}

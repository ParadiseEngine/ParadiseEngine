using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;
using Paradise.Windowing;

namespace Paradise.Ui.ImGui;

/// <summary>The renderer-independent half of Dear ImGui, shared by every host (the SDL/WebGPU
/// runtime, a Godot play-mode bridge, the editor):
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread and owns the ENTIRE
///   ImGui frame: events feed <c>io</c>, and each fixed tick runs NewFrame → registered draw
///   delegates → Render → snapshot. Immediate mode plus sim-thread execution means panels read
///   and mutate live sim state directly — no marshaling.
/// - The host's render half never touches ImGui at all. It takes the latest self-contained
///   <see cref="ImGuiDrawSnapshot"/> from <see cref="AcquireSnapshotForRender"/> (triple-buffered
///   handoff, so neither thread waits on the other beyond a pointer swap) and applies
///   its texture ops before drawing it with whatever renderer it owns.
///
/// <b>Two handoffs, because they have opposite requirements.</b> Snapshots are droppable — the
/// newest wins and the rest are recycled. Texture ops are not, and they are ordered; see
/// <see cref="ImGuiFrameExchange"/>. Both are filled here, on the sim thread, inside
/// <see cref="IUiInput.Tick"/>.
///
/// Context creation happens on the main thread before the sim starts; ImGui's current context
/// lives in cimgui's process-global <c>GImGui</c>, so there is no thread affinity — only a
/// no-concurrent-access rule, and after startup only the sim thread calls into it.
/// Lifetime is normally the process (one global ImGui context), and <see cref="Dispose"/> exists
/// for the cases that are not — an editor that tears a session down, and the test suite.</summary>
public sealed class ImGuiUiCore : IDisposable
{
    private readonly ImGuiFrameExchange _exchange = new();
    private ImGuiContextPtr _context;
    private readonly List<Action> _draw = new();
    private double _lastTickTime;
    private bool _hasTicked;

    // Clipboard bridge. ImGui's callbacks fire on the SIM thread mid-frame, but the real system
    // clipboard belongs to the host's platform thread (SDL/Godot clipboard APIs are main-thread),
    // so the two sides meet in a lock-guarded cache: the host pushes the system text in via
    // SetHostClipboard BEFORE forwarding a paste chord, and drains UI-copied text out via
    // TryTakeClipboardCopy to publish it. The static instance mirrors the process-global ImGui
    // context (UnmanagedCallersOnly trampolines need a static hop).
    private static ImGuiUiCore? s_clipboardOwner;
    private readonly object _clipboardLock = new();
    private string _clipboardFromHost = string.Empty;
    private string? _clipboardToHost;
    private nint _clipboardUtf8;

    /// <summary>The sim-thread half. Hand this to the host's input pump (directly, or stacked
    /// with other UI systems through <see cref="CompositeUiInput"/>).</summary>
    public IUiInput Input { get; }


    /// <param name="pixelWidth">Initial display width in pixels; resizes arrive as
    /// <see cref="WindowEventKind.Resize"/> events.</param>
    /// <param name="pixelHeight">Initial display height in pixels.</param>
    /// <param name="font">Optional font to load in place of ImGui's ASCII-only default, carrying
    /// the mount it is read out of (see <see cref="UiFonts"/>). Under the 1.92 texture protocol
    /// glyphs rasterize ON DEMAND, so a CJK-capable font here costs nothing until CJK text is
    /// actually drawn — there are no glyph ranges to declare and no atlas size to budget. The
    /// bytes are handed to ImGui and freed with the context. When the font cannot be loaded
    /// the core degrades to the default font.</param>
    public unsafe ImGuiUiCore(uint pixelWidth, uint pixelHeight, UiFontConfig? font = null)
    {
        _context = ImGuiApi.CreateContext();
        ImGuiApi.SetCurrentContext(_context);
        var io = ImGuiApi.GetIO();
        // RendererHasTextures is not optional on 1.92: without it ImGui expects a backend that
        // builds a static atlas itself, and the obsolete API that would do so is not in this
        // binding at all — NewFrame asserts "font atlas is not built".
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(pixelWidth, pixelHeight);
        // Text-editing shortcuts stay Ctrl-based on every OS. cimgui's Apple build defaults this
        // to true (Cmd-based shortcuts), but hosts map Cmd onto Control in the WindowEvent
        // stream, so paste is Ctrl+V uniformly.
        io.ConfigMacOSXBehaviors = false;
        // Replace cimgui's built-in clipboard handler (an app-private buffer on macOS/Linux)
        // with the host-bridged cache — without this, pasting from other applications can never
        // work.
        s_clipboardOwner = this;
        var platformIo = ImGuiApi.GetPlatformIO();
        platformIo.PlatformGetClipboardTextFn = (delegate* unmanaged<nint, byte*>)&GetClipboardTextCallback;
        platformIo.PlatformSetClipboardTextFn = (delegate* unmanaged<nint, byte*, void>)&SetClipboardTextCallback;
        if (font is null || !UiFonts.TryAddFont(io, font))
        {
            io.Fonts.AddFontDefault();
        }

        Input = new UiInputHalf(this);
    }

    /// <summary>Register a per-tick draw delegate — runs ON THE SIM THREAD between NewFrame and
    /// Render, so it may read and mutate sim-owned state freely. Register before the sim
    /// starts.</summary>
    public void AddDraw(Action draw) => _draw.Add(draw);

    /// <summary>Destroy the ImGui context and release the clipboard bridge. Call on the thread
    /// that owns the context, after the sim has stopped ticking — every member of this class
    /// reads process-global ImGui state that this frees.</summary>
    public void Dispose()
    {
        if (_context.IsNull) return;
        ImGuiApi.DestroyContext(_context);
        _context = default;
        ImGuiApi.SetCurrentContext(default);
        if (ReferenceEquals(s_clipboardOwner, this)) s_clipboardOwner = null;
        Marshal.FreeCoTaskMem(_clipboardUtf8);
        _clipboardUtf8 = nint.Zero;
    }

    /// <summary>Host/platform thread: push the current system clipboard text so the next Ctrl+V
    /// processed on the sim thread pastes it. Hosts call this right before forwarding a paste
    /// chord (the event queue preserves the ordering).</summary>
    public void SetHostClipboard(string text)
    {
        lock (_clipboardLock) _clipboardFromHost = text;
    }

    /// <summary>Host/platform thread: drain text the UI copied (Ctrl+C/X in a text field) so the
    /// host can publish it to the system clipboard. False when nothing new was copied — poll
    /// once per host frame.</summary>
    public bool TryTakeClipboardCopy(out string text)
    {
        lock (_clipboardLock)
        {
            text = _clipboardToHost ?? string.Empty;
            var hasCopy = _clipboardToHost is not null;
            _clipboardToHost = null;
            return hasCopy;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe byte* GetClipboardTextCallback(nint imguiContext) =>
        s_clipboardOwner is { } core ? (byte*)core.RentClipboardUtf8() : null;

    [UnmanagedCallersOnly]
    private static unsafe void SetClipboardTextCallback(nint imguiContext, byte* utf8Text)
    {
        if (s_clipboardOwner is { } core && utf8Text is not null)
        {
            core.StoreClipboardCopy(Marshal.PtrToStringUTF8((nint)utf8Text) ?? string.Empty);
        }
    }

    /// <summary>Sim thread (ImGui paste). The returned buffer must outlive the call — ImGui reads
    /// it immediately after — so it is kept until the next paste replaces it.</summary>
    private nint RentClipboardUtf8()
    {
        lock (_clipboardLock)
        {
            Marshal.FreeCoTaskMem(_clipboardUtf8);
            _clipboardUtf8 = Marshal.StringToCoTaskMemUTF8(_clipboardFromHost);
            return _clipboardUtf8;
        }
    }

    private void StoreClipboardCopy(string text)
    {
        lock (_clipboardLock)
        {
            _clipboardToHost = text;
            // Mirror into the paste cache so copy → paste inside the app works this frame, before
            // the host has round-tripped the text through the system clipboard.
            _clipboardFromHost = text;
        }
    }

    /// <summary>Render/main-thread half: take the newest frame — the snapshot to draw plus every
    /// texture operation not yet applied. Null before the first sim tick.
    ///
    /// Apply <paramref name="textureOps"/> (<c>ImGuiWebGpuRenderer.ApplyTextureOps</c>) before
    /// drawing, EVERY frame: a repeat snapshot still needs the glyphs that arrived since. See
    /// <see cref="ImGuiFrameExchange"/> for why the two travel together.</summary>
    public ImGuiDrawSnapshot? AcquireSnapshotForRender(List<ImGuiTextureOp> textureOps, out bool isNew) =>
        _exchange.AcquireForRender(textureOps, out isNew);

    private sealed class UiInputHalf(ImGuiUiCore owner) : IUiInput
    {
        public bool Handle(in WindowEvent input)
        {
            var io = ImGuiApi.GetIO();
            switch (input.Kind)
            {
                case WindowEventKind.PointerMove:
                    io.AddMousePosEvent(input.X, input.Y);
                    return io.WantCaptureMouse;

                case WindowEventKind.Button when input.Source is EventSource.Mouse or EventSource.Touch:
                    // The position rides along on a pointer-button event; feeding it first places
                    // the click correctly even for a host that reports no preceding move (a
                    // touchscreen tap has none).
                    io.AddMousePosEvent(input.X, input.Y);
                    io.AddMouseButtonEvent((int)input.PointerButton, input.Pressed);
                    return io.WantCaptureMouse;

                case WindowEventKind.Button when input.Source == EventSource.Keyboard:
                    // An unmapped key must report "not handled" WITHOUT touching io, or the host
                    // reads a false consumption and withholds the key from the game.
                    if (ToImGui(input.KeyboardKey) is not { } key) return false;
                    io.AddKeyEvent(key, input.Pressed);
                    return io.WantCaptureKeyboard;

                case WindowEventKind.Scroll:
                    io.AddMouseWheelEvent(input.X, input.Y);
                    return io.WantCaptureMouse;

                case WindowEventKind.Text:
                    io.AddInputCharacter(input.Character);
                    return io.WantCaptureKeyboard;

                case WindowEventKind.Resize:
                    io.DisplaySize = new Vector2(input.X, input.Y);
                    return false;

                default:
                    return false; // axes and gamepad buttons have no meaning to this core
            }
        }

        public void Tick(double simTimeSeconds)
        {
            var io = ImGuiApi.GetIO();
            // Seed on the first tick: the sim clock is not guaranteed to start at zero, and an
            // unclamped first DeltaTime would step animations by the whole absolute time.
            var delta = owner._hasTicked ? simTimeSeconds - owner._lastTickTime : 0.0;
            owner._lastTickTime = simTimeSeconds;
            owner._hasTicked = true;
            io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f;

            ImGuiApi.NewFrame();
            foreach (var draw in owner._draw)
            {
                draw();
            }
            ImGuiApi.Render();

            var drawData = ImGuiApi.GetDrawData();
            // Textures before geometry, in both senses: the capture stamps the ImTextureID the
            // draw commands carry, and enqueuing the ops before the snapshot is published is what
            // lets ImGuiFrameExchange guarantee the renderer holds them (see its remarks).
            ImGuiTextureCapture.CaptureFrom(drawData, owner._exchange.TextureOps);

            var snapshot = owner._exchange.Rent();
            snapshot.Capture(drawData);
            owner._exchange.Publish(snapshot);
        }

        /// <summary>The engine's key vocabulary in ImGui's. Every key ImGui names is mapped:
        /// this core backs an editor, where an unmapped key is a shortcut that silently does
        /// nothing. Keys the engine reports but ImGui has no name for return null and are left
        /// to the game.</summary>
        private static ImGuiKey? ToImGui(KeyboardKey key) => key switch
        {
            KeyboardKey.A => ImGuiKey.A,
            KeyboardKey.B => ImGuiKey.B,
            KeyboardKey.C => ImGuiKey.C,
            KeyboardKey.D => ImGuiKey.D,
            KeyboardKey.E => ImGuiKey.E,
            KeyboardKey.F => ImGuiKey.F,
            KeyboardKey.G => ImGuiKey.G,
            KeyboardKey.H => ImGuiKey.H,
            KeyboardKey.I => ImGuiKey.I,
            KeyboardKey.J => ImGuiKey.J,
            KeyboardKey.K => ImGuiKey.K,
            KeyboardKey.L => ImGuiKey.L,
            KeyboardKey.M => ImGuiKey.M,
            KeyboardKey.N => ImGuiKey.N,
            KeyboardKey.O => ImGuiKey.O,
            KeyboardKey.P => ImGuiKey.P,
            KeyboardKey.Q => ImGuiKey.Q,
            KeyboardKey.R => ImGuiKey.R,
            KeyboardKey.S => ImGuiKey.S,
            KeyboardKey.T => ImGuiKey.T,
            KeyboardKey.U => ImGuiKey.U,
            KeyboardKey.V => ImGuiKey.V,
            KeyboardKey.W => ImGuiKey.W,
            KeyboardKey.X => ImGuiKey.X,
            KeyboardKey.Y => ImGuiKey.Y,
            KeyboardKey.Z => ImGuiKey.Z,

            KeyboardKey.Digit0 => ImGuiKey.Key0,
            KeyboardKey.Digit1 => ImGuiKey.Key1,
            KeyboardKey.Digit2 => ImGuiKey.Key2,
            KeyboardKey.Digit3 => ImGuiKey.Key3,
            KeyboardKey.Digit4 => ImGuiKey.Key4,
            KeyboardKey.Digit5 => ImGuiKey.Key5,
            KeyboardKey.Digit6 => ImGuiKey.Key6,
            KeyboardKey.Digit7 => ImGuiKey.Key7,
            KeyboardKey.Digit8 => ImGuiKey.Key8,
            KeyboardKey.Digit9 => ImGuiKey.Key9,

            KeyboardKey.F1 => ImGuiKey.F1,
            KeyboardKey.F2 => ImGuiKey.F2,
            KeyboardKey.F3 => ImGuiKey.F3,
            KeyboardKey.F4 => ImGuiKey.F4,
            KeyboardKey.F5 => ImGuiKey.F5,
            KeyboardKey.F6 => ImGuiKey.F6,
            KeyboardKey.F7 => ImGuiKey.F7,
            KeyboardKey.F8 => ImGuiKey.F8,
            KeyboardKey.F9 => ImGuiKey.F9,
            KeyboardKey.F10 => ImGuiKey.F10,
            KeyboardKey.F11 => ImGuiKey.F11,
            KeyboardKey.F12 => ImGuiKey.F12,

            KeyboardKey.Up => ImGuiKey.UpArrow,
            KeyboardKey.Down => ImGuiKey.DownArrow,
            KeyboardKey.Left => ImGuiKey.LeftArrow,
            KeyboardKey.Right => ImGuiKey.RightArrow,

            KeyboardKey.Space => ImGuiKey.Space,
            KeyboardKey.Enter => ImGuiKey.Enter,
            KeyboardKey.Escape => ImGuiKey.Escape,
            KeyboardKey.Tab => ImGuiKey.Tab,
            KeyboardKey.Backspace => ImGuiKey.Backspace,
            KeyboardKey.Delete => ImGuiKey.Delete,
            KeyboardKey.Insert => ImGuiKey.Insert,
            KeyboardKey.Home => ImGuiKey.Home,
            KeyboardKey.End => ImGuiKey.End,
            KeyboardKey.PageUp => ImGuiKey.PageUp,
            KeyboardKey.PageDown => ImGuiKey.PageDown,

            // Sided modifiers only. Since 1.89 ImGui derives io.KeyCtrl/Shift/Alt/Super from
            // these itself, so sending ModCtrl alongside would double-report the chord.
            KeyboardKey.LeftShift => ImGuiKey.LeftShift,
            KeyboardKey.RightShift => ImGuiKey.RightShift,
            KeyboardKey.LeftControl => ImGuiKey.LeftCtrl,
            KeyboardKey.RightControl => ImGuiKey.RightCtrl,
            KeyboardKey.LeftAlt => ImGuiKey.LeftAlt,
            KeyboardKey.RightAlt => ImGuiKey.RightAlt,
            KeyboardKey.LeftMeta => ImGuiKey.LeftSuper,
            KeyboardKey.RightMeta => ImGuiKey.RightSuper,

            KeyboardKey.Minus => ImGuiKey.Minus,
            KeyboardKey.Equals => ImGuiKey.Equal,
            KeyboardKey.LeftBracket => ImGuiKey.LeftBracket,
            KeyboardKey.RightBracket => ImGuiKey.RightBracket,
            KeyboardKey.Backslash => ImGuiKey.Backslash,
            KeyboardKey.Semicolon => ImGuiKey.Semicolon,
            KeyboardKey.Apostrophe => ImGuiKey.Apostrophe,
            KeyboardKey.Grave => ImGuiKey.GraveAccent,
            KeyboardKey.Comma => ImGuiKey.Comma,
            KeyboardKey.Period => ImGuiKey.Period,
            KeyboardKey.Slash => ImGuiKey.Slash,

            KeyboardKey.Numpad0 => ImGuiKey.Keypad0,
            KeyboardKey.Numpad1 => ImGuiKey.Keypad1,
            KeyboardKey.Numpad2 => ImGuiKey.Keypad2,
            KeyboardKey.Numpad3 => ImGuiKey.Keypad3,
            KeyboardKey.Numpad4 => ImGuiKey.Keypad4,
            KeyboardKey.Numpad5 => ImGuiKey.Keypad5,
            KeyboardKey.Numpad6 => ImGuiKey.Keypad6,
            KeyboardKey.Numpad7 => ImGuiKey.Keypad7,
            KeyboardKey.Numpad8 => ImGuiKey.Keypad8,
            KeyboardKey.Numpad9 => ImGuiKey.Keypad9,
            KeyboardKey.NumpadDivide => ImGuiKey.KeypadDivide,
            KeyboardKey.NumpadMultiply => ImGuiKey.KeypadMultiply,
            KeyboardKey.NumpadMinus => ImGuiKey.KeypadSubtract,
            KeyboardKey.NumpadPlus => ImGuiKey.KeypadAdd,
            KeyboardKey.NumpadEnter => ImGuiKey.KeypadEnter,
            KeyboardKey.NumpadPeriod => ImGuiKey.KeypadDecimal,
            KeyboardKey.NumLock => ImGuiKey.NumLock,

            KeyboardKey.CapsLock => ImGuiKey.CapsLock,
            KeyboardKey.PrintScreen => ImGuiKey.PrintScreen,
            KeyboardKey.ScrollLock => ImGuiKey.ScrollLock,
            KeyboardKey.Pause => ImGuiKey.Pause,

            _ => null,
        };
    }
}

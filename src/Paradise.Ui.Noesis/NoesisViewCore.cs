using Noesis;
using Paradise.Windowing;
using IoPath = System.IO.Path;

namespace Paradise.Ui.Noesis;

/// <summary>The renderer-independent half of NoesisGUI in the two-half UI architecture,
/// shared by every host (an SDL/WebGPU runtime, a Godot play-mode bridge):
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread — the simulation
///   drains pointer, key and text events into the view and advances view time each fixed tick,
///   so hover, focus, animations and bindings step in lockstep with game state. Handle's return
///   value is the view's own verdict — true only when the UI actually consumed the event — which
///   is what lets a host route unconsumed input onward to gameplay without guessing.
/// - The host's render half (a WebGPU overlay pass, or an offscreen render + readback) reads
///   <see cref="View"/> once published, initializes its own <c>RenderDevice</c> against it,
///   and calls <c>TryUpdateRenderTree</c> once per frame before recording the passes
///   (<see cref="NoesisOverlayRenderer"/> packages that half for OverlayPass hosts).
///
/// The two halves meet at exactly one point, per Noesis's threading model: view updates
/// (sim) and <c>UpdateRenderTree</c> (render) are mutually excluded by the internal sync
/// lock — which is why the render half goes through <c>TryUpdateRenderTree</c> instead
/// of touching the renderer directly; <c>Renderer.Init/RenderOffscreen/Render</c> touch only
/// render-side state and deliberately stay outside the lock.
///
/// Noesis pins each View to the Dispatcher of its CREATION thread, so all GUI construction
/// (native init, providers rooted at the XAML's directory, optional
/// <c>Theme/NoesisTheme.DarkBlue.xaml</c>, view creation) happens LAZILY on the sim thread at
/// the first tick; render halves wait (skipping frames) until <see cref="View"/> is
/// published.</summary>
// Lifetime: process-scoped by design — no Dispose/GUI.Shutdown. Hosts create at most one
// NoesisViewCore per process and native/GPU teardown happens at exit; add disposal if this
// ever hosts multiple sessions (tests, editor).
public sealed class NoesisViewCore
{
    private readonly string _root;
    private readonly string _xamlFile;
    private readonly object _sync = new();
    private readonly object? _dataContext;
    private readonly Action? _simTick;
    private readonly string? _licenseName;
    private readonly string? _licenseKey;
    private static bool s_globalInitialized; // GUI.Init/license/log are process-global, once
    private volatile View? _view; // published by the sim thread once created there
    private bool _pendingSnapshot; // an Update produced a frame the render side has not taken
    private volatile uint _width;
    private volatile uint _height;

    public IUiInput Input { get; }

    /// <summary>The Noesis view, once the sim thread has created it; null until then (render
    /// halves skip frames). Volatile read.</summary>
    public View? View => _view;

    /// <summary>Current view size in UI pixels — tracks sim-side Resize events. Volatile.</summary>
    public uint Width => _width;
    public uint Height => _height;

    /// <param name="dataContext">Optional root DataContext for the loaded XAML (an MVVM
    /// ViewModel) — applied on the sim thread before the view is created.</param>
    /// <param name="simTick">Optional per-tick refresh hook, run on the SIM thread under the
    /// view sync lock right before <c>View.Update</c> — the place to project game state into
    /// the ViewModel (Noesis binding updates must happen on the view's creation thread).</param>
    /// <param name="licenseName">NoesisGUI license name; falls back to the
    /// <c>NOESIS_LICENSE_NAME</c> environment variable when null.</param>
    /// <param name="licenseKey">NoesisGUI license key; falls back to the
    /// <c>NOESIS_LICENSE_KEY</c> environment variable when null.</param>
    public NoesisViewCore(string xamlPath, uint pixelWidth, uint pixelHeight,
        object? dataContext = null, Action? simTick = null,
        string? licenseName = null, string? licenseKey = null)
    {
        _root = IoPath.GetDirectoryName(IoPath.GetFullPath(xamlPath)) ?? ".";
        _xamlFile = IoPath.GetFileName(xamlPath);
        _width = pixelWidth;
        _height = pixelHeight;
        _dataContext = dataContext;
        _simTick = simTick;
        _licenseName = licenseName ?? Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        _licenseKey = licenseKey ?? Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        Input = new UiInputHalf(this);
    }

    /// <summary>The single sync point between the halves: pick up the last view update into the
    /// render tree. False while the view does not exist yet. Call from the render thread once
    /// per frame, before recording the UI passes.</summary>
    public bool TryUpdateRenderTree() => TryUpdateRenderTree(out _);

    /// <summary>As <see cref="TryUpdateRenderTree()"/>, and reports whether the render tree
    /// actually CHANGED since the last frame.
    ///
    /// Use it to skip work only if you are drawing into a target that PERSISTS between frames.
    /// A host compositing through an OverlayPass must not: its backbuffer is a fresh swapchain
    /// texture every frame, so skipping the UI passes on an unchanged frame does not reuse the
    /// last image, it presents one with no UI at all — a flicker whose rate depends on how
    /// still the UI is, which is a memorable way to spend an afternoon.</summary>
    public bool TryUpdateRenderTree(out bool changed)
    {
        changed = false;
        var view = _view;
        if (view is null) return false;
        lock (_sync)
        {
            changed = view.Renderer.UpdateRenderTree();
            // The snapshot (if any) has been taken; the UI thread may produce the next one.
            _pendingSnapshot = false;
        }
        return true;
    }

    /// <summary>Sim-thread (lazy) construction: the View's Dispatcher binds here, making the
    /// sim thread the UI thread for its whole lifetime.</summary>
    private View CreateViewOnSimThread()
    {
        if (!s_globalInitialized)
        {
            Log.SetLogCallback(static (level, channel, message) =>
            {
                if (level >= global::Noesis.LogLevel.Warning) Console.WriteLine($"[noesis {level}] {message}");
            });
            if (!string.IsNullOrWhiteSpace(_licenseName) && !string.IsNullOrWhiteSpace(_licenseKey))
            {
                GUI.SetLicense(_licenseName, _licenseKey);
            }
            GUI.Init();
            s_globalInitialized = true;
        }
        // Providers are process-global in Noesis: the most recently created core owns them.
        // Fine for the intended one-core-per-process hosts; a second core re-roots XAML
        // loading (and hot reload) at its own directory from this point on.
        GUI.SetXamlProvider(new FolderXamlProvider(_root));
        GUI.SetTextureProvider(new FolderTextureProvider(_root));
        GUI.SetFontProvider(new FolderFontProvider(_root));
        GUI.SetFontDefaultProperties(14.0f, FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
        if (File.Exists(IoPath.Combine(_root, "Theme", "NoesisTheme.DarkBlue.xaml")))
        {
            GUI.SetFontFallbacks(["Theme/Fonts/#PT Root UI", "Arial"]);
            GUI.LoadApplicationResources("Theme/NoesisTheme.DarkBlue.xaml");
        }

        var rootElement = (FrameworkElement)GUI.LoadXaml(_xamlFile);
        if (_dataContext is not null)
        {
            rootElement.DataContext = _dataContext;
        }
        var view = GUI.CreateView(rootElement);
        view.SetFlags(RenderFlags.PPAA);
        view.SetSize((int)_width, (int)_height);
        // Name the OWNING thread, not "the sim thread": which thread creates the view is the
        // host's choice (a sim-thread UI, or a render-thread one whose ViewModel reads
        // presentation state directly), and it is pinned here for the view's whole life — so
        // the log has to say which one it actually was.
        Console.WriteLine($"[NoesisUi] '{_xamlFile}' loaded from {_root} ({_width}x{_height}) "
            + $"on thread '{Thread.CurrentThread.Name ?? "unnamed"}' — the view is pinned to it.");
        return view;
    }

    private sealed class UiInputHalf(NoesisViewCore owner) : IUiInput
    {
        /// <summary>One wheel notch, in the Win32/WPF units Noesis expects — three lines of
        /// scrolling. Hosts report scroll deltas in notches, so this is the conversion.</summary>
        private const float NotchUnits = 120f;

        // Noesis hit-tests a wheel event at a point, but the WindowEvent contract reuses X/Y for the
        // scroll delta — so the last pointer position is what a scroll is aimed at.
        private int _pointerX;
        private int _pointerY;
        // Precise pointing devices (a MacBook trackpad, a free-spinning wheel) report small
        // fractions of a notch per event. Carrying the sub-unit remainder instead of truncating
        // it away is what makes a slow two-finger scroll move at all.
        private float _pendingHorizontal;
        private float _pendingVertical;

        private View SimView => owner._view ??= owner.CreateViewOnSimThread();

        public bool Handle(in WindowEvent raw)
        {
            lock (owner._sync)
            {
                var view = SimView;
                switch (raw.Kind)
                {
                    case WindowEventKind.PointerMove:
                        return view.MouseMove(TrackX(raw.X), TrackY(raw.Y));

                    case WindowEventKind.Button when raw.Source is EventSource.Mouse or EventSource.Touch:
                        return raw.Pressed
                            ? view.MouseButtonDown(TrackX(raw.X), TrackY(raw.Y), ToNoesis(raw.PointerButton))
                            : view.MouseButtonUp(TrackX(raw.X), TrackY(raw.Y), ToNoesis(raw.PointerButton));

                    case WindowEventKind.Button when raw.Source == EventSource.Keyboard:
                        // Only a mapped key may consume: an unmapped one must report "not
                        // handled" WITHOUT touching the view, or the host reads a false
                        // consumption and withholds the key from the game.
                        return ToNoesis(raw.KeyboardKey) is { } key
                            && (raw.Pressed ? view.KeyDown(key) : view.KeyUp(key));

                    case WindowEventKind.Scroll:
                    {
                        // X/Y are the delta in notches: +Y is a wheel rotated forward (scroll up),
                        // +X is a wheel rotated right — both matching Noesis's own sign convention.
                        var handled = false;
                        if (TakeRotation(raw.Y, ref _pendingVertical) is { } vertical)
                        {
                            handled |= view.MouseWheel(_pointerX, _pointerY, vertical);
                        }
                        if (TakeRotation(raw.X, ref _pendingHorizontal) is { } horizontal)
                        {
                            handled |= view.MouseHWheel(_pointerX, _pointerY, horizontal);
                        }
                        return handled;
                    }

                    case WindowEventKind.Text:
                        // A lone surrogate is not a character; Noesis would read it as one.
                        return IsUnicodeScalar(raw.Character) && view.Char(raw.Character);

                    case WindowEventKind.Resize:
                        owner._width = (uint)raw.X;
                        owner._height = (uint)raw.Y;
                        view.SetSize((int)raw.X, (int)raw.Y);
                        return false;

                    default:
                        return false; // axes and gamepad buttons have no UI meaning
                }
            }
        }

        /// <summary>Advance UI time, unless the render side has not taken the last frame yet.
        ///
        /// That guard is the documented contract, not caution: Noesis says <c>Update</c> "never
        /// blocks and allocates memory when not synchronized with UpdateRenderTree", so every
        /// Update that returns true and is not matched by an UpdateRenderTree queues a snapshot
        /// that is never collected. A host can drop frames for ordinary reasons — a minimized
        /// window, a lost swapchain, any frame that returns before its overlay pass — and with
        /// a UI that changes every tick (a clock, a counter) those unmatched Updates accumulate
        /// for as long as it stays minimized. Skipping instead is free and correct: there is no
        /// one to show the frame to, and time is passed absolutely, so the next Update that does
        /// run lands on the right moment rather than replaying the backlog.</summary>
        public void Tick(double simTimeSeconds)
        {
            lock (owner._sync)
            {
                var view = SimView; // created here on first touch, before the pending check
                if (owner._pendingSnapshot)
                {
                    return;
                }
                owner._simTick?.Invoke();
                owner._pendingSnapshot = view.Update(simTimeSeconds);
            }
        }

        private int TrackX(float x) => _pointerX = (int)x;
        private int TrackY(float y) => _pointerY = (int)y;

        /// <summary>Converts a delta in notches to whole Noesis wheel units, banking whatever
        /// does not fill a unit for the next event. Null when nothing whole came out.</summary>
        private static int? TakeRotation(float notches, ref float pending)
        {
            pending += notches * NotchUnits;
            var rotation = (int)pending; // truncates toward zero, so the sign is preserved
            if (rotation == 0) return null;
            pending -= rotation;
            return rotation;
        }

        private static MouseButton ToNoesis(PointerButton button) => button switch
        {
            PointerButton.Right => MouseButton.Right,
            PointerButton.Middle => MouseButton.Middle,
            PointerButton.X1 => MouseButton.XButton1,
            PointerButton.X2 => MouseButton.XButton2,
            _ => MouseButton.Left,
        };

        /// <summary>The windowing contract's keys → Noesis's. Total over the vocabulary a UI
        /// can act on, so a host forwards whatever it already has and nothing has to be
        /// re-mapped downstream. Anything outside it — and <see cref="KeyboardKey.None"/> —
        /// returns null so the caller reports "not handled" WITHOUT touching the view; an
        /// unmapped key must never consume input. Noesis follows WPF's naming, so Enter is
        /// <c>Return</c>, Backspace is <c>Back</c> and the digits are <c>D0</c>-<c>D9</c>.
        ///
        /// WHICH keys a UI is allowed to see is deliberately NOT decided here — that is the
        /// host's policy, and a game that forwards [W] has handed movement to whatever holds
        /// focus.</summary>
        private static Key? ToNoesis(KeyboardKey key) => key switch
        {
            KeyboardKey.Enter => Key.Return,
            KeyboardKey.NumpadEnter => Key.Return,
            KeyboardKey.Escape => Key.Escape,
            KeyboardKey.Backspace => Key.Back,
            KeyboardKey.Delete => Key.Delete,
            KeyboardKey.Insert => Key.Insert,
            KeyboardKey.Tab => Key.Tab,
            KeyboardKey.Space => Key.Space,
            KeyboardKey.Left => Key.Left,
            KeyboardKey.Right => Key.Right,
            KeyboardKey.Up => Key.Up,
            KeyboardKey.Down => Key.Down,
            KeyboardKey.Home => Key.Home,
            KeyboardKey.End => Key.End,
            KeyboardKey.PageUp => Key.PageUp,
            KeyboardKey.PageDown => Key.PageDown,
            KeyboardKey.LeftControl => Key.LeftCtrl,
            KeyboardKey.RightControl => Key.RightCtrl,
            KeyboardKey.LeftShift => Key.LeftShift,
            KeyboardKey.RightShift => Key.RightShift,
            KeyboardKey.LeftAlt => Key.LeftAlt,
            KeyboardKey.RightAlt => Key.RightAlt,
            KeyboardKey.Digit0 => Key.D0,
            KeyboardKey.Digit1 => Key.D1,
            KeyboardKey.Digit2 => Key.D2,
            KeyboardKey.Digit3 => Key.D3,
            KeyboardKey.Digit4 => Key.D4,
            KeyboardKey.Digit5 => Key.D5,
            KeyboardKey.Digit6 => Key.D6,
            KeyboardKey.Digit7 => Key.D7,
            KeyboardKey.Digit8 => Key.D8,
            KeyboardKey.Digit9 => Key.D9,
            KeyboardKey.F1 => Key.F1,
            KeyboardKey.F2 => Key.F2,
            KeyboardKey.F3 => Key.F3,
            KeyboardKey.F4 => Key.F4,
            KeyboardKey.F5 => Key.F5,
            KeyboardKey.F6 => Key.F6,
            KeyboardKey.F7 => Key.F7,
            KeyboardKey.F8 => Key.F8,
            KeyboardKey.F9 => Key.F9,
            KeyboardKey.F10 => Key.F10,
            KeyboardKey.F11 => Key.F11,
            KeyboardKey.F12 => Key.F12,
            KeyboardKey.A => Key.A,
            KeyboardKey.B => Key.B,
            KeyboardKey.C => Key.C,
            KeyboardKey.D => Key.D,
            KeyboardKey.E => Key.E,
            KeyboardKey.F => Key.F,
            KeyboardKey.G => Key.G,
            KeyboardKey.H => Key.H,
            KeyboardKey.I => Key.I,
            KeyboardKey.J => Key.J,
            KeyboardKey.K => Key.K,
            KeyboardKey.L => Key.L,
            KeyboardKey.M => Key.M,
            KeyboardKey.N => Key.N,
            KeyboardKey.O => Key.O,
            KeyboardKey.P => Key.P,
            KeyboardKey.Q => Key.Q,
            KeyboardKey.R => Key.R,
            KeyboardKey.S => Key.S,
            KeyboardKey.T => Key.T,
            KeyboardKey.U => Key.U,
            KeyboardKey.V => Key.V,
            KeyboardKey.W => Key.W,
            KeyboardKey.X => Key.X,
            KeyboardKey.Y => Key.Y,
            KeyboardKey.Z => Key.Z,
            _ => null,
        };

        private static bool IsUnicodeScalar(uint value) =>
            value <= 0x10FFFF && value is not (>= 0xD800 and <= 0xDFFF);
    }

    // ---- file-system resource providers rooted at the XAML's directory ----

    private static string Combine(string root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var normalized = segment.Replace('\\', IoPath.DirectorySeparatorChar)
                .Replace('/', IoPath.DirectorySeparatorChar)
                .TrimStart(IoPath.DirectorySeparatorChar);
            if (normalized.Length > 0) path = IoPath.Combine(path, normalized);
        }
        return path;
    }

    private sealed class FolderXamlProvider(string root) : XamlProvider
    {
        public override Stream? LoadXaml(Uri uri)
        {
            var path = Combine(root, uri.GetPath());
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }

    private sealed class FolderTextureProvider(string root) : FileTextureProvider
    {
        public override Stream? OpenStream(Uri uri)
        {
            var path = Combine(root, uri.GetPath());
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }

    private sealed class FolderFontProvider(string root) : FontProvider
    {
        public override Stream? OpenFont(Uri folder, string filename)
        {
            var path = Combine(root, folder.GetPath(), filename);
            return File.Exists(path) ? File.OpenRead(path) : null;
        }

        public override void ScanFolder(Uri folder)
        {
            var path = Combine(root, folder.GetPath());
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path))
            {
                var ext = IoPath.GetExtension(file);
                if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterFont(folder, IoPath.GetFileName(file));
                }
            }
        }
    }
}

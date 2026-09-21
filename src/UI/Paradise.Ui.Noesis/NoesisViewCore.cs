using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Noesis;
using Paradise.Windowing;
using Zio;
using Zio.FileSystems;

// Noesis has its own LogLevel, and `using Noesis;` above makes the bare name ambiguous.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Paradise.Ui.Noesis;

/// <summary>Coordinates Noesis input, view updates and render-tree transfer.</summary>
/// <remarks>Input and Tick run on the simulation thread; TryUpdateRenderTree serializes the
/// renderer handoff with them. Renderer.Init and Render calls use render-side state outside that
/// lock. GUI construction is deferred to the first simulation tick because Noesis binds the view to
/// its creation thread; rendering waits for publication.</remarks>
// Lifetime: process-scoped by design — no Dispose/GUI.Shutdown. Hosts create at most one
// NoesisViewCore per process and native/GPU teardown happens at exit; add disposal if this
// ever hosts multiple sessions (tests, editor).
public sealed partial class NoesisViewCore
{
    private readonly IFileSystem _content;
    private readonly UPath _root;
    /// <summary>Where the UI tree came FROM, for the log line only: _root is "/" inside the
    /// re-mount, which tells a reader nothing about which directory was loaded.</summary>
    private readonly UPath _origin;
    private readonly string _xamlFile;
    private readonly object _sync = new();
    private readonly object? _dataContext;
    private readonly Action? _simTick;
    private readonly string? _licenseName;
    private readonly string? _licenseKey;
    private readonly ILogger _log;
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

    /// <summary>Creates a Noesis view using resources rooted at the XAML directory.</summary>
    /// <remarks>Providers borrow a submount of the host filesystem, enforcing resource containment.
    /// Place shared Theme/Fonts beneath the XAML directory; the view does not own the host
    /// mount.</remarks>
    /// <param name="dataContext">Optional root DataContext for the loaded XAML (an MVVM
    /// ViewModel) — applied on the sim thread before the view is created.</param>
    /// <param name="simTick">Optional per-tick refresh hook, run on the SIM thread under the
    /// view sync lock right before <c>View.Update</c> — the place to project game state into
    /// the ViewModel (Noesis binding updates must happen on the view's creation thread).</param>
    /// <param name="licenseName">NoesisGUI license name; falls back to the
    /// <c>NOESIS_LICENSE_NAME</c> environment variable when null.</param>
    /// <param name="licenseKey">NoesisGUI license key; falls back to the
    /// <c>NOESIS_LICENSE_KEY</c> environment variable when null.</param>
    /// <param name="content">The mount the UI tree lives in. A host mounts a directory; a test
    /// mounts memory and needs no fixture files on disk at all.</param>
    /// <param name="xamlPath">The root XAML, as a path in <paramref name="content"/>. Its
    /// DIRECTORY becomes this view's whole world — see the remarks.</param>
    public NoesisViewCore(IFileSystem content, UPath xamlPath, uint pixelWidth, uint pixelHeight,
        object? dataContext = null, Action? simTick = null,
        string? licenseName = null, string? licenseKey = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        // Named HERE rather than left to Zio, which rejects a relative path at first USE — and
        // first use is the lazy construction on the sim thread, a long way from this argument.
        if (!xamlPath.IsAbsolute)
        {
            throw new ArgumentException(
                $"'{xamlPath}' must be absolute in the mount: a UPath is rooted at the mount, "
                + "not at a working directory.", nameof(xamlPath));
        }

        _content = new SubFileSystem(content, xamlPath.GetDirectory(), owned: false);
        _root = UPath.Root;
        _origin = xamlPath.GetDirectory();
        _xamlFile = xamlPath.GetName();
        _width = pixelWidth;
        _height = pixelHeight;
        _dataContext = dataContext;
        _simTick = simTick;
        _licenseName = licenseName ?? Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        _licenseKey = licenseKey ?? Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        _log = logger ?? NullLogger.Instance;
        Input = new UiInputHalf(this);
    }

    /// <summary>The single sync point between the halves: pick up the last view update into the
    /// render tree. False while the view does not exist yet. Call from the render thread once
    /// per frame, before recording the UI passes.</summary>
    public bool TryUpdateRenderTree() => TryUpdateRenderTree(out _);

    /// <summary>Attempts a render-tree update and reports whether it changed.</summary>
    /// <remarks>Skip drawing unchanged content only when the target persists between frames; a
    /// fresh swapchain still needs every overlay pass.</remarks>
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
            // Captures THIS core's logger, and Noesis's callback is process-global — so with two
            // cores in one process the first one created owns Noesis's log for the whole run.
            // That matches what the surrounding code already says about providers being global;
            // it is a caveat rather than a bug, and there is no per-core seam in Noesis to use.
            var log = _log;
            Log.SetLogCallback((level, channel, message) =>
            {
                if (level < global::Noesis.LogLevel.Warning) return;
                LogFromNoesis(log, ToLogLevel(level), channel, message);
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
        GUI.SetXamlProvider(new FolderXamlProvider(_content, _root));
        GUI.SetTextureProvider(new FolderTextureProvider(_content, _root));
        GUI.SetFontProvider(new FolderFontProvider(_content, _root));
        GUI.SetFontDefaultProperties(14.0f, FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
        if (_content.FileExists(_root / "Theme" / "NoesisTheme.DarkBlue.xaml"))
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
        LogViewLoaded(_log, _xamlFile, _origin, _width, _height, Thread.CurrentThread.Name ?? "unnamed");
        return view;
    }

    /// <summary>Noesis's severity, in the vocabulary the rest of the engine logs in.</summary>
    /// <remarks>Only Warning and above reach here; the lower Noesis levels are filtered at the
    /// callback because Noesis is chatty enough at Trace to cost real time in a frame.</remarks>
    private static LogLevel ToLogLevel(global::Noesis.LogLevel level) => level switch
    {
        global::Noesis.LogLevel.Error => LogLevel.Error,
        global::Noesis.LogLevel.Warning => LogLevel.Warning,
        global::Noesis.LogLevel.Info => LogLevel.Information,
        global::Noesis.LogLevel.Debug => LogLevel.Debug,
        _ => LogLevel.Trace,
    };

    [LoggerMessage(EventId = 80, Message = "[{Channel}] {Message}")]
    private static partial void LogFromNoesis(ILogger logger, LogLevel level, string channel, string message);

    /// <remarks>Names the OWNING thread, not "the sim thread": which thread creates the view is
    /// the host's choice, and it is pinned for the view's whole life — so the log has to say which
    /// one it actually was.</remarks>
    [LoggerMessage(
        EventId = 81,
        Level = LogLevel.Information,
        Message = "'{XamlFile}' loaded from {Origin} ({Width}x{Height}) on thread '{Thread}' — the view is pinned to it.")]
    private static partial void LogViewLoaded(
        ILogger logger, string xamlFile, UPath origin, uint width, uint height, string thread);

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
                    {
                        var x = TrackX(raw.X);
                        var y = TrackY(raw.Y);
                        if (!raw.Pressed) return view.MouseButtonUp(x, y, ToNoesis(raw.PointerButton));

                        // Noesis reports every press consumed, even over empty views. Hit-test
                        // before forwarding: the press may change focus or open a popup. Other
                        // event kinds retain the toolkit verdict.
                        var consumed = OverSomething(view, x, y);
                        view.MouseButtonDown(x, y, ToNoesis(raw.PointerButton));
                        return consumed;
                    }

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

        /// <summary>Advances UI time when the previous view snapshot has been consumed.</summary>
        /// <remarks>Unmatched successful Update calls allocate queued snapshots. Skipping prevents
        /// buildup while rendering is paused; absolute time lets the next update catch
        /// up.</remarks>
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

        /// <summary>Tests whether an input-eligible visual lies under the pointer.</summary>
        /// <remarks>Noesis's visual hit test includes noninteractive subtrees, so filter
        /// IsHitTestVisible=false explicitly. Keep disabled controls hit-testable to block clicks
        /// through dialogs. Decorative elements should opt out; roots should use a null
        /// background.</remarks>
        private static bool OverSomething(View view, int x, int y)
        {
            if (view.Content is not Visual root) return false;

            var hit = false;
            VisualTreeHelper.HitTest(
                root,
                visual => visual is UIElement { IsHitTestVisible: false }
                    ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                    : HitTestFilterBehavior.Continue,
                _ =>
                {
                    hit = true;
                    return HitTestResultBehavior.Stop; // the topmost one settles it
                },
                new PointHitTestParameters(new Point(x, y)));
            return hit;
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

        /// <summary>Maps supported window keys to Noesis keys, returning null for unmapped
        /// input.</summary>
        /// <remarks>The host decides which keys reach the UI; an unmapped key must remain
        /// unconsumed.</remarks>
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

    // ---- content-mount resource providers rooted at the XAML's directory ----

    /// <summary>Resolves a resource URI beneath the view root, returning null when
    /// invalid.</summary>
    /// <remarks>Trim leading separators for root-relative URIs. Path errors must return
    /// missing-resource results because providers are called from native Noesis code.</remarks>
    private static UPath? Combine(UPath root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var normalized = segment.Replace('\\', UPath.DirectorySeparator)
                .TrimStart(UPath.DirectorySeparator);
            if (normalized.Length == 0) continue;

            try
            {
                path /= normalized;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return path;
    }

    private sealed class FolderXamlProvider(IFileSystem content, UPath root) : XamlProvider
    {
        public override Stream? LoadXaml(Uri uri)
        {
            return Combine(root, uri.GetPath()) is { } path && content.FileExists(path)
                ? content.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
        }
    }

    private sealed class FolderTextureProvider(IFileSystem content, UPath root) : FileTextureProvider
    {
        public override Stream? OpenStream(Uri uri)
        {
            return Combine(root, uri.GetPath()) is { } path && content.FileExists(path)
                ? content.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
        }
    }

    private sealed class FolderFontProvider(IFileSystem content, UPath root) : FontProvider
    {
        public override Stream? OpenFont(Uri folder, string filename)
        {
            return Combine(root, folder.GetPath(), filename) is { } path && content.FileExists(path)
                ? content.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
        }

        public override void ScanFolder(Uri folder)
        {
            if (Combine(root, folder.GetPath()) is not { } path || !content.DirectoryExists(path)) return;
            foreach (var file in content.EnumerateFiles(path))
            {
                var ext = file.GetExtensionWithDot();
                if (".ttf".Equals(ext, StringComparison.OrdinalIgnoreCase)
                    || ".otf".Equals(ext, StringComparison.OrdinalIgnoreCase))
                {
                    RegisterFont(folder, file.GetName());
                }
            }
        }
    }
}

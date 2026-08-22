using Noesis;
using IoPath = System.IO.Path;

namespace Paradise.Ui.Noesis.Host;

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
///   and calls <see cref="TryUpdateRenderTree"/> once per frame before recording the passes
///   (<see cref="NoesisOverlayRenderer"/> packages that half for OverlayPass hosts).
///
/// The two halves meet at exactly one point, per Noesis's threading model: view updates
/// (sim) and <c>UpdateRenderTree</c> (render) are mutually excluded by the internal sync
/// lock — which is why the render half goes through <see cref="TryUpdateRenderTree"/> instead
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

        // Noesis hit-tests a wheel event at a point, but the UiEvent contract reuses X/Y for the
        // scroll delta — so the last pointer position is what a scroll is aimed at.
        private int _pointerX;
        private int _pointerY;
        // Precise pointing devices (a MacBook trackpad, a free-spinning wheel) report small
        // fractions of a notch per event. Carrying the sub-unit remainder instead of truncating
        // it away is what makes a slow two-finger scroll move at all.
        private float _pendingHorizontal;
        private float _pendingVertical;

        private View SimView => owner._view ??= owner.CreateViewOnSimThread();

        public bool Handle(in UiEvent uiEvent)
        {
            lock (owner._sync)
            {
                var view = SimView;
                switch (uiEvent.Kind)
                {
                    case UiEventKind.PointerMove:
                        return view.MouseMove(TrackX(uiEvent.X), TrackY(uiEvent.Y));
                    case UiEventKind.PointerDown:
                        return view.MouseButtonDown(TrackX(uiEvent.X), TrackY(uiEvent.Y), ToNoesis(uiEvent.Button));
                    case UiEventKind.PointerUp:
                        return view.MouseButtonUp(TrackX(uiEvent.X), TrackY(uiEvent.Y), ToNoesis(uiEvent.Button));
                    case UiEventKind.Scroll:
                    {
                        // X/Y are the delta in notches: +Y is a wheel rotated forward (scroll up),
                        // +X is a wheel rotated right — both matching Noesis's own sign convention.
                        var handled = false;
                        if (TakeRotation(uiEvent.Y, ref _pendingVertical) is { } vertical)
                        {
                            handled |= view.MouseWheel(_pointerX, _pointerY, vertical);
                        }
                        if (TakeRotation(uiEvent.X, ref _pendingHorizontal) is { } horizontal)
                        {
                            handled |= view.MouseHWheel(_pointerX, _pointerY, horizontal);
                        }
                        return handled;
                    }
                    case UiEventKind.Resize:
                        owner._width = (uint)uiEvent.X;
                        owner._height = (uint)uiEvent.Y;
                        view.SetSize((int)uiEvent.X, (int)uiEvent.Y);
                        return false;

                    // Keyboard and text are what make a Noesis menu focusable rather than
                    // merely clickable — and the verdict they return is the whole point: Noesis
                    // answers true only when a FOCUSED element handled the key, so with nothing
                    // focused a gameplay key passes straight through to the game. The host
                    // never has to guess whether the UI wanted it.
                    case UiEventKind.KeyDown:
                        return ToNoesis(uiEvent.Key) is { } down && view.KeyDown(down);
                    case UiEventKind.KeyUp:
                        return ToNoesis(uiEvent.Key) is { } up && view.KeyUp(up);
                    case UiEventKind.Text:
                        // A lone surrogate is not a character; Noesis would read it as one.
                        return IsUnicodeScalar(uiEvent.Character) && view.Char(uiEvent.Character);

                    default:
                        return false;
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

        private static MouseButton ToNoesis(UiPointerButton button) => button switch
        {
            UiPointerButton.Right => MouseButton.Right,
            UiPointerButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        /// <summary>The contract's key vocabulary → Noesis's. Deliberately partial: the
        /// <see cref="UiKey"/> set is only what a text field and a menu need, and anything
        /// outside it (including <see cref="UiKey.None"/>) returns null so the caller reports
        /// "not handled" WITHOUT touching the view — an unmapped key must not consume input.
        /// Noesis follows WPF's naming, so Enter is <c>Return</c> and Backspace is
        /// <c>Back</c>.</summary>
        private static Key? ToNoesis(UiKey key) => key switch
        {
            UiKey.Enter => Key.Return,
            UiKey.Escape => Key.Escape,
            UiKey.Backspace => Key.Back,
            UiKey.Delete => Key.Delete,
            UiKey.Tab => Key.Tab,
            UiKey.Left => Key.Left,
            UiKey.Right => Key.Right,
            UiKey.Up => Key.Up,
            UiKey.Down => Key.Down,
            UiKey.Home => Key.Home,
            UiKey.End => Key.End,
            UiKey.Ctrl => Key.LeftCtrl,
            UiKey.Shift => Key.LeftShift,
            UiKey.A => Key.A,
            UiKey.C => Key.C,
            UiKey.D => Key.D,
            UiKey.S => Key.S,
            UiKey.V => Key.V,
            UiKey.W => Key.W,
            UiKey.X => Key.X,
            UiKey.Y => Key.Y,
            UiKey.Z => Key.Z,
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

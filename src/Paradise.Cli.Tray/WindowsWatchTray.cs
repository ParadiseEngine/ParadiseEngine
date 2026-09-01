using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paradise.Cli;

// DllImport rather than LibraryImport: WNDCLASSEX carries a managed WndProc delegate and
// NOTIFYICONDATA carries ByValTStr buffers, neither of which the source generator can emit.
// The runtime marshaller is the right tool here, and SYSLIB1054 would otherwise fail the
// build (warnings-as-errors) for every entry point.
#pragma warning disable SYSLIB1054

/// <summary>
/// A notify icon via <c>Shell_NotifyIconW</c>. Lives on its own STA thread because the shell
/// delivers clicks through a window procedure, and a console's main thread has no message pump.
/// </summary>
/// <remarks>
/// Failure anywhere in startup (no explorer, session 0, RegisterClass refused) returns
/// <see langword="null"/> from <see cref="TryStart"/> and the watch continues without an icon.
/// That is the additive contract: the tray is allowed to not exist.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWatchTray : IWatchTray
{
    private const uint WmApp = 0x8000;
    private const uint MsgNotify = WmApp + 1;
    private const uint MsgSetState = WmApp + 2;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolwindow = 0x00000080;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdLastBuild = 1;
    private const int IdRebuild = 2;
    private const int IdOpen = 3;
    private const int IdStop = 4;
    private const int IconSize = 16;
    private const uint NotifyId = 1;

    private static readonly (byte R, byte G, byte B) AliveColor = (0x6B, 0x8C, 0xA8);
    private static readonly (byte R, byte G, byte B) IdleColor = (0x3D, 0xC7, 0x5E);
    private static readonly (byte R, byte G, byte B) BuildingColor = (0xF5, 0xC5, 0x18);
    private static readonly (byte R, byte G, byte B) FailedColor = (0xE8, 0x4E, 0x4E);

    private readonly WatchTrayHooks _hooks;
    private readonly Native.WndProc _wndProc;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private nint _hwnd;
    private string? _className;
    private nint _iconAlive;
    private nint _iconIdle;
    private nint _iconBuilding;
    private nint _iconFailed;
    private WatchStatus _status = WatchStatus.Alive;
    private int _errorCount;
    private bool _added;
    private bool _disposed;
    private volatile bool _abandoned;

    private WindowsWatchTray(WatchTrayHooks hooks)
    {
        _hooks = hooks;
        // Stored on the instance so the GC cannot collect the thunk while user32 still calls it.
        _wndProc = WindowProc;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "paradise-watch-tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Show a tray icon, or <see langword="null"/> if the shell refused.</summary>
    public static WindowsWatchTray? TryStart(WatchTrayHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        try
        {
            var tray = new WindowsWatchTray(hooks);
            if (!tray._ready.Task.Wait(TimeSpan.FromSeconds(3)) || !tray._ready.Task.Result)
            {
                tray.Dispose();
                return null;
            }

            return tray;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool IsAvailable => true;

    public void SetState(WatchStatus status, int errorCount)
    {
        var hwnd = _hwnd;
        if (hwnd != 0)
        {
            Native.PostMessage(hwnd, MsgSetState, (nuint)(int)status, errorCount);
        }
    }

    public void Run(Action watch, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(watch);
        log?.Invoke("watch: tray icon is up (right-click to stop, rebuild, or open the build folder)");
        watch();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _abandoned = true;

        var hwnd = _hwnd;
        if (hwnd != 0)
        {
            Native.PostMessage(hwnd, WmClose, 0, 0);
        }
        else
        {
            _ready.TrySetResult(false);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Pump()
    {
        try
        {
            _className = "ParadiseWatchTray" + Guid.NewGuid().ToString("N");
            var hInstance = Native.GetModuleHandle(null);
            var wndClass = new Native.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Native.WndClassEx>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = _className,
            };

            var atom = Native.RegisterClassEx(in wndClass);
            if (atom == 0)
            {
                _ready.TrySetResult(false);
                return;
            }

            var hwnd = Native.CreateWindowEx(
                WsExToolwindow,
                _className,
                "paradise watch",
                WsPopup,
                0, 0, 0, 0,
                nint.Zero, nint.Zero, hInstance, nint.Zero);
            if (hwnd == 0)
            {
                _ready.TrySetResult(false);
                return;
            }

            _hwnd = hwnd;
            _iconAlive = CreateDot(AliveColor.R, AliveColor.G, AliveColor.B);
            _iconIdle = CreateDot(IdleColor.R, IdleColor.G, IdleColor.B);
            _iconBuilding = CreateDot(BuildingColor.R, BuildingColor.G, BuildingColor.B);
            _iconFailed = CreateDot(FailedColor.R, FailedColor.G, FailedColor.B);
            if (_iconAlive == 0 || _iconIdle == 0 || _iconBuilding == 0 || _iconFailed == 0)
            {
                _ready.TrySetResult(false);
                Native.DestroyWindow(hwnd);
                return;
            }

            if (!Notify(NimAdd))
            {
                _ready.TrySetResult(false);
                Native.DestroyWindow(hwnd);
                return;
            }

            if (_abandoned)
            {
                Notify(NimDelete);
                Native.DestroyWindow(hwnd);
                _ready.TrySetResult(false);
                return;
            }

            _added = true;
            _ready.TrySetResult(true);

            while (Native.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);
            }
        }
        catch
        {
            _ready.TrySetResult(false);
        }
        finally
        {
            Teardown();
        }
    }

    private nint WindowProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case MsgNotify:
                    var eventMsg = (uint)lParam;
                    if (eventMsg is WmRButtonUp or WmLButtonUp or WmContextMenu)
                    {
                        ShowMenu(hWnd);
                    }

                    return 0;

                case MsgSetState:
                    _status = (WatchStatus)(int)wParam;
                    _errorCount = (int)lParam;
                    if (_added) Notify(NimModify);
                    return 0;

                case WmClose:
                    Native.DestroyWindow(hWnd);
                    return 0;

                case WmDestroy:
                    if (_added)
                    {
                        Notify(NimDelete);
                        _added = false;
                    }

                    Native.PostQuitMessage(0);
                    return 0;
            }

            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }
        catch
        {
            // Reverse P/Invoke: a managed exception through user32 can FailFast or tear the
            // tray thread down. The watch loop is the feature; the icon is a satellite.
            return 0;
        }
    }

    private void ShowMenu(nint hWnd)
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            Native.AppendMenu(menu, MfString | MfGrayed, IdLastBuild, WatchPresentation.LastBuildMenu(_status, _errorCount));
            Native.AppendMenu(menu, MfSeparator, 0, string.Empty);
            if (_hooks.Rebuild is not null)
            {
                Native.AppendMenu(menu, MfString, IdRebuild, "Rebuild now");
            }

            Native.AppendMenu(menu, MfString, IdOpen, "Open the build folder");
            Native.AppendMenu(menu, MfSeparator, 0, string.Empty);
            Native.AppendMenu(menu, MfString, IdStop, "Stop");

            Native.GetCursorPos(out var point);
            Native.SetForegroundWindow(hWnd);
            var chosen = Native.TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, hWnd, nint.Zero);
            Native.PostMessage(hWnd, 0, 0, 0);

            switch (chosen)
            {
                case IdRebuild:
                    _hooks.Rebuild?.Invoke();
                    break;
                case IdOpen:
                    _hooks.OpenOutput();
                    break;
                case IdStop:
                    _hooks.Stop();
                    break;
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private bool Notify(uint message)
    {
        var data = new Native.NotifyIconData
        {
            cbSize = Marshal.SizeOf<Native.NotifyIconData>(),
            hWnd = _hwnd,
            uID = NotifyId,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = MsgNotify,
            hIcon = IconFor(_status),
            szTip = WatchPresentation.Tooltip(_status, _errorCount),
        };
        return Native.Shell_NotifyIcon(message, in data) != 0;
    }

    private nint IconFor(WatchStatus status) => status switch
    {
        WatchStatus.Idle => _iconIdle,
        WatchStatus.Building => _iconBuilding,
        WatchStatus.Failed => _iconFailed,
        _ => _iconAlive,
    };

    private void Teardown()
    {
        _hwnd = 0;
        DestroyIcon(ref _iconAlive);
        DestroyIcon(ref _iconIdle);
        DestroyIcon(ref _iconBuilding);
        DestroyIcon(ref _iconFailed);

        if (_className is not null)
        {
            Native.UnregisterClass(_className, Native.GetModuleHandle(null));
            _className = null;
        }
    }

    private static void DestroyIcon(ref nint icon)
    {
        if (icon == 0) return;
        Native.DestroyIcon(icon);
        icon = 0;
    }

    private static nint CreateDot(byte red, byte green, byte blue)
    {
        var info = new Native.BitmapInfo
        {
            bmiHeader = new Native.BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<Native.BitmapInfoHeader>(),
                biWidth = IconSize,
                biHeight = -IconSize,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        var color = Native.CreateDIBSection(nint.Zero, in info, 0, out var bits, nint.Zero, 0);
        if (color == 0 || bits == 0) return 0;

        unsafe
        {
            var pixels = (uint*)bits;
            const float Center = (IconSize - 1) / 2f;
            const float Radius = IconSize / 2f - 0.5f;
            var radiusSq = Radius * Radius;
            var argb = (uint)(0xFF << 24 | red << 16 | green << 8 | blue);
            for (var y = 0; y < IconSize; y++)
            {
                for (var x = 0; x < IconSize; x++)
                {
                    var dx = x - Center;
                    var dy = y - Center;
                    pixels[y * IconSize + x] = dx * dx + dy * dy <= radiusSq ? argb : 0;
                }
            }
        }

        nint mask;
        unsafe
        {
            var maskBits = stackalloc byte[IconSize * 4];
            mask = Native.CreateBitmap(IconSize, IconSize, 1, 1, (nint)maskBits);
        }
        if (mask == 0)
        {
            Native.DeleteObject(color);
            return 0;
        }

        var iconInfo = new Native.IconInfo
        {
            fIcon = 1,
            hbmMask = mask,
            hbmColor = color,
        };
        var icon = Native.CreateIconIndirect(in iconInfo);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }

    private static class Native
    {
        public delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
        {
            public int cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public nint hwnd;
            public uint message;
            public nuint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo
        {
            public BitmapInfoHeader bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IconInfo
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public nint hbmMask;
            public nint hbmColor;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
        public static extern ushort RegisterClassEx(in WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW", SetLastError = true)]
        public static extern int UnregisterClass(string lpClassName, nint hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
        public static extern nint CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int DestroyWindow(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        public static extern nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern int TranslateMessage(in Msg lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
        public static extern nint DispatchMessage(in Msg lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        public static extern int AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern int DestroyMenu(nint hMenu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

        [DllImport("user32.dll")]
        public static extern int SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        public static extern int GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int DestroyIcon(nint hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint CreateIconIndirect(in IconInfo piconinfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        public static extern nint GetModuleHandle(string? lpModuleName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        public static extern int Shell_NotifyIcon(uint dwMessage, in NotifyIconData lpData);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint CreateDIBSection(
            nint hdc, in BitmapInfo pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

        [DllImport("gdi32.dll")]
        public static extern int DeleteObject(nint ho);
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paradise.Cli;

// DllImport rather than LibraryImport: objc_msgSend is a family of signatures the source
// generator cannot emit as one entry point. SYSLIB1054 would otherwise fail the build
// (warnings-as-errors) for every overload.
#pragma warning disable SYSLIB1054

/// <summary>
/// An <c>NSStatusItem</c> in the macOS menu bar. AppKit owns the thread that calls
/// <see cref="Run"/>: the watch loop runs on a worker, then <c>[NSApp run]</c> blocks until
/// stop. Failure to load AppKit falls back to the console loop on this thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacWatchTray : IWatchTray
{
    private const string TargetClassName = "ParadiseWatchTrayTarget";
    private const int NsApplicationActivationPolicyAccessory = 1;
    private const int RtldNow = 2;

    private static readonly NFloat VariableStatusItemLength = -1;
    private static MacWatchTray? s_current;

    private readonly WatchTrayHooks _hooks;
    private readonly object _gate = new();

    private nint _nsApp;
    private nint _nsThreadClass;
    private nint _nsStringClass;
    private nint _target;
    private nint _statusBar;
    private nint _statusItem;
    private nint _menu;
    private nint _lastBuildItem;
    private nint _selSetTitle;
    private nint _selSetToolTip;
    private nint _selButton;
    private nint _selIsMainThread;
    private nint _selPerformSelectorOnMainThread;
    private nint _selApplyPendingState;
    private nint _selStopApp;
    private nint _selRun;
    private nint _selStop;
    private nint _selStringWithUTF8String;

    private WatchStatus _status = WatchStatus.Alive;
    private int _errorCount;
    private bool _bootstrapped;
    private bool _disposed;

    public MacWatchTray(WatchTrayHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _hooks = hooks;
        s_current = this;
    }

    public bool IsAvailable => _bootstrapped;

    public void SetState(WatchStatus status, int errorCount)
    {
        lock (_gate)
        {
            _status = status;
            _errorCount = errorCount;
        }

        HopApply();
    }

    public void Run(Action watch, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(watch);

        if (!TryBootstrap(out var error))
        {
            log?.Invoke($"watch: {error}; continuing in the console");
            watch();
            return;
        }

        log?.Invoke("watch: tray icon is up (click to stop, rebuild, or open the build folder)");

        var watchThread = new Thread(() =>
        {
            try
            {
                watch();
            }
            finally
            {
                StopNsApp();
            }
        })
        {
            IsBackground = true,
            Name = "paradise-watch",
        };
        watchThread.Start();

        Native.MsgSend(_nsApp, _selRun);
        watchThread.Join();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_bootstrapped)
        {
            StopNsApp();
            Teardown();
        }

        if (ReferenceEquals(s_current, this)) s_current = null;
    }

    private bool TryBootstrap(out string error)
    {
        error = "could not start the menu-bar icon";
        try
        {
            if (Native.dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RtldNow) == 0
                && Native.dlopen("/System/Library/Frameworks/AppKit.framework/Versions/Current/AppKit", RtldNow) == 0)
            {
                error = "could not load AppKit";
                return false;
            }

            var nsApplication = Native.objc_getClass("NSApplication");
            _nsStringClass = Native.objc_getClass("NSString");
            _nsThreadClass = Native.objc_getClass("NSThread");
            var nsStatusBar = Native.objc_getClass("NSStatusBar");
            var nsMenu = Native.objc_getClass("NSMenu");
            var nsMenuItem = Native.objc_getClass("NSMenuItem");
            if (nsApplication == 0 || _nsStringClass == 0 || _nsThreadClass == 0
                || nsStatusBar == 0 || nsMenu == 0 || nsMenuItem == 0)
            {
                error = "AppKit classes are missing";
                return false;
            }

            _selSetTitle = Sel("setTitle:");
            _selSetToolTip = Sel("setToolTip:");
            _selButton = Sel("button");
            _selIsMainThread = Sel("isMainThread");
            _selPerformSelectorOnMainThread = Sel("performSelectorOnMainThread:withObject:waitUntilDone:");
            _selApplyPendingState = Sel("applyPendingState");
            _selStopApp = Sel("stopApp:");
            _selRun = Sel("run");
            _selStop = Sel("stop:");
            _selStringWithUTF8String = Sel("stringWithUTF8String:");
            var selAlloc = Sel("alloc");
            var selInit = Sel("init");
            var selInitWithTitle = Sel("initWithTitle:");
            var selInitWithTitleActionKey = Sel("initWithTitle:action:keyEquivalent:");
            var selAddItem = Sel("addItem:");
            var selSetEnabled = Sel("setEnabled:");
            var selSetTarget = Sel("setTarget:");
            var selSetMenu = Sel("setMenu:");
            var selSharedApplication = Sel("sharedApplication");
            var selSetActivationPolicy = Sel("setActivationPolicy:");
            var selSystemStatusBar = Sel("systemStatusBar");
            var selStatusItemWithLength = Sel("statusItemWithLength:");
            var selSeparatorItem = Sel("separatorItem");
            var selRebuild = Sel("rebuildClicked:");
            var selOpen = Sel("openClicked:");
            var selQuit = Sel("stopClicked:");

            var pool = Native.objc_autoreleasePoolPush();
            try
            {
                _nsApp = Native.MsgSend(nsApplication, selSharedApplication);
                if (_nsApp == 0)
                {
                    error = "NSApplication is missing";
                    return false;
                }

                Native.MsgSend(_nsApp, selSetActivationPolicy, NsApplicationActivationPolicyAccessory);

                var targetClass = Native.objc_lookUpClass(TargetClassName);
                if (targetClass == 0)
                {
                    var nsObject = Native.objc_getClass("NSObject");
                    targetClass = Native.objc_allocateClassPair(nsObject, TargetClassName, 0);
                    if (targetClass == 0)
                    {
                        error = "could not create the menu target class";
                        return false;
                    }

                    unsafe
                    {
                        AddMethod(targetClass, selRebuild, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RebuildImp, "v@:@");
                        AddMethod(targetClass, selOpen, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OpenImp, "v@:@");
                        AddMethod(targetClass, selQuit, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&StopClickedImp, "v@:@");
                        AddMethod(targetClass, _selApplyPendingState, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ApplyImp, "v@:");
                        AddMethod(targetClass, _selStopApp, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&StopAppImp, "v@:@");
                    }

                    Native.objc_registerClassPair(targetClass);
                }

                _target = Native.MsgSend(Native.MsgSend(targetClass, selAlloc), selInit);
                if (_target == 0)
                {
                    error = "could not allocate the menu target";
                    return false;
                }

                _statusBar = Native.MsgSend(nsStatusBar, selSystemStatusBar);
                _statusItem = Native.objc_retain(Native.MsgSendNFloat(_statusBar, selStatusItemWithLength, VariableStatusItemLength));
                if (_statusItem == 0)
                {
                    error = "could not create an NSStatusItem";
                    return false;
                }

                _menu = Native.MsgSend(Native.MsgSend(nsMenu, selAlloc), selInitWithTitle, ToNSString("paradise watch"));
                _lastBuildItem = Native.MsgSend3(
                    Native.MsgSend(nsMenuItem, selAlloc),
                    selInitWithTitleActionKey,
                    ToNSString(WatchPresentation.LastBuildMenu(_status, _errorCount)),
                    0,
                    ToNSString(""));
                Native.MsgSendByte(_lastBuildItem, selSetEnabled, 0);
                Native.MsgSend(_menu, selAddItem, _lastBuildItem);
                Native.MsgSend(_menu, selAddItem, Native.MsgSend(nsMenuItem, selSeparatorItem));

                if (_hooks.Rebuild is not null)
                {
                    AddMenuItem(nsMenuItem, selAlloc, selInitWithTitleActionKey, selSetTarget, selAddItem, "Rebuild now", selRebuild);
                }

                AddMenuItem(nsMenuItem, selAlloc, selInitWithTitleActionKey, selSetTarget, selAddItem, "Open the build folder", selOpen);
                Native.MsgSend(_menu, selAddItem, Native.MsgSend(nsMenuItem, selSeparatorItem));
                AddMenuItem(nsMenuItem, selAlloc, selInitWithTitleActionKey, selSetTarget, selAddItem, "Stop", selQuit);
                Native.MsgSend(_statusItem, selSetMenu, _menu);

                _bootstrapped = true;
                ApplyOnMainThread();
                return true;
            }
            finally
            {
                Native.objc_autoreleasePoolPop(pool);
                if (!_bootstrapped) Teardown();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "AppKit entry points are missing";
            return false;
        }
    }

    private void AddMenuItem(
        nint nsMenuItem,
        nint selAlloc,
        nint selInitWithTitleActionKey,
        nint selSetTarget,
        nint selAddItem,
        string title,
        nint action)
    {
        var item = Native.MsgSend3(
            Native.MsgSend(nsMenuItem, selAlloc),
            selInitWithTitleActionKey,
            ToNSString(title),
            action,
            ToNSString(""));
        Native.MsgSend(item, selSetTarget, _target);
        Native.MsgSend(_menu, selAddItem, item);
    }

    private static void AddMethod(nint cls, nint selector, nint imp, string types)
        => Native.class_addMethod(cls, selector, imp, types);

    private void HopApply()
    {
        if (!_bootstrapped || _target == 0) return;
        if (IsMainThread())
        {
            ApplyOnMainThread();
            return;
        }

        Native.MsgSendSelObjByte(_target, _selPerformSelectorOnMainThread, _selApplyPendingState, 0, 0);
    }

    private void ApplyOnMainThread()
    {
        if (!_bootstrapped || _statusItem == 0) return;

        WatchStatus status;
        int errorCount;
        lock (_gate)
        {
            status = _status;
            errorCount = _errorCount;
        }

        var pool = Native.objc_autoreleasePoolPush();
        try
        {
            var title = ToNSString(WatchPresentation.MenuBarTitle(status));
            var tip = ToNSString(WatchPresentation.Tooltip(status, errorCount));
            var button = Native.MsgSend(_statusItem, _selButton);
            if (button != 0)
            {
                Native.MsgSend(button, _selSetTitle, title);
                Native.MsgSend(button, _selSetToolTip, tip);
            }
            else
            {
                Native.MsgSend(_statusItem, _selSetTitle, title);
                Native.MsgSend(_statusItem, _selSetToolTip, tip);
            }

            if (_lastBuildItem != 0)
            {
                Native.MsgSend(_lastBuildItem, _selSetTitle, ToNSString(WatchPresentation.LastBuildMenu(status, errorCount)));
            }
        }
        finally
        {
            Native.objc_autoreleasePoolPop(pool);
        }
    }

    private void StopNsApp()
    {
        Native.CFRunLoopStop(Native.CFRunLoopGetMain());
        var app = _nsApp;
        if (app == 0) return;
        if (_bootstrapped && IsMainThread())
        {
            Native.MsgSend(app, _selStop, 0);
            return;
        }

        if (_target != 0)
        {
            Native.MsgSendSelObjByte(_target, _selPerformSelectorOnMainThread, _selStopApp, 0, 0);
        }
    }

    private void Teardown()
    {
        var pool = Native.objc_autoreleasePoolPush();
        try
        {
            if (_statusBar != 0 && _statusItem != 0)
            {
                Native.MsgSend(_statusBar, Sel("removeStatusItem:"), _statusItem);
            }

            if (_statusItem != 0)
            {
                Native.objc_release(_statusItem);
                _statusItem = 0;
            }

            if (_menu != 0)
            {
                Native.objc_release(_menu);
                _menu = 0;
            }

            if (_target != 0)
            {
                Native.objc_release(_target);
                _target = 0;
            }

            _lastBuildItem = 0;
            _bootstrapped = false;
        }
        finally
        {
            Native.objc_autoreleasePoolPop(pool);
        }
    }

    private bool IsMainThread()
        => _nsThreadClass != 0 && Native.MsgSendRetByte(_nsThreadClass, _selIsMainThread) != 0;

    private nint ToNSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Native.MsgSend(_nsStringClass, _selStringWithUTF8String, utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static nint Sel(string name) => Native.sel_registerName(name);

#pragma warning disable IDE0060 // objc IMPs must match (id, SEL[, sender]) even when the arguments are unused

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RebuildImp(nint self, nint cmd, nint sender)
    {
        try
        {
            s_current?._hooks.Rebuild?.Invoke();
        }
        catch
        {
            // Menu IMPs must not throw back into AppKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OpenImp(nint self, nint cmd, nint sender)
    {
        try
        {
            s_current?._hooks.OpenOutput();
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StopClickedImp(nint self, nint cmd, nint sender)
    {
        try
        {
            s_current?._hooks.Stop();
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ApplyImp(nint self, nint cmd)
    {
        try
        {
            s_current?.ApplyOnMainThread();
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StopAppImp(nint self, nint cmd, nint sender)
    {
        try
        {
            var tray = s_current;
            if (tray is null || tray._nsApp == 0) return;
            Native.MsgSend(tray._nsApp, tray._selStop, 0);
        }
        catch
        {
        }
    }

#pragma warning restore IDE0060

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(LibSystem, CharSet = CharSet.Ansi)]
        public static extern nint dlopen(string path, int mode);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        public static extern nint sel_registerName(string name);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        public static extern nint objc_getClass(string name);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        public static extern nint objc_lookUpClass(string name);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        public static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

        [DllImport(ObjC)]
        public static extern void objc_registerClassPair(nint cls);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        public static extern byte class_addMethod(nint cls, nint selector, nint imp, string types);

        [DllImport(ObjC)]
        public static extern nint objc_retain(nint value);

        [DllImport(ObjC)]
        public static extern void objc_release(nint value);

        [DllImport(ObjC)]
        public static extern nint objc_autoreleasePoolPush();

        [DllImport(ObjC)]
        public static extern void objc_autoreleasePoolPop(nint pool);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern nint MsgSend(nint receiver, nint selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern nint MsgSend(nint receiver, nint selector, nint arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern nint MsgSend3(nint receiver, nint selector, nint a, nint b, nint c);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern nint MsgSendNFloat(nint receiver, nint selector, NFloat arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern void MsgSendByte(nint receiver, nint selector, byte arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern void MsgSendSelObjByte(nint receiver, nint selector, nint a, nint b, byte c);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        public static extern byte MsgSendRetByte(nint receiver, nint selector);

        [DllImport(CoreFoundation)]
        public static extern nint CFRunLoopGetMain();

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopStop(nint runLoop);
    }
}

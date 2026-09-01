using System.Runtime.Versioning;

namespace Paradise.Cli;

/// <summary>
/// Constructs a tray if the OS can show one, and a no-op otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Issue #192 considered three shapes: a cross-platform tray package, Win32
/// <c>Shell_NotifyIcon</c> behind an OS guard, and a <c>net10.0-windows</c> WinForms split.
/// The package direction was preferred <b>if one actually worked</b> outside Windows without
/// pulling a GUI toolkit into a console <c>PackAsTool</c>. Checking that: Avalonia's tray is
/// real but is a GUI framework; H.NotifyIcon is NativeAOT-friendly and works in a console app,
/// but it is Windows-only; Linux D-Bus wrappers are young and bring Skia. None of those keep
/// the tool's TFM and dependency set intact. Win32 P/Invoke behind an OS guard does.
/// </para>
/// <para>
/// The tray is strictly additive. Headless, CI, <c>--no-tray</c>, and every non-Windows OS
/// take the no-op path, and <c>watch</c> then behaves as it did before the icon existed.
/// </para>
/// </remarks>
internal static class WatchTray
{
    /// <summary>
    /// Try to show a tray. Never throws: a notify-icon failure must not take the watch down
    /// with it, because the console loop is the feature and the icon is a satellite.
    /// </summary>
    public static IWatchTray Create(WatchTrayHooks hooks, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        if (!enabled) return NullWatchTray.Instance;

        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(hooks);
        }

        return NullWatchTray.Instance;
    }

    [SupportedOSPlatform("windows")]
    private static IWatchTray CreateWindows(WatchTrayHooks hooks)
        => (IWatchTray?)WindowsWatchTray.TryStart(hooks) ?? NullWatchTray.Instance;
}

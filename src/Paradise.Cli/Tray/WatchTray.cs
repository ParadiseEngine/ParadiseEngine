using System.Runtime.Versioning;

namespace Paradise.Cli;

/// <summary>
/// Constructs a tray if a desktop session can show one, and a no-op otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Two native renderings, one factory: Win32 <c>Shell_NotifyIcon</c> on Windows (its own STA
/// pump, because a console thread has no message loop) and AppKit <c>NSStatusItem</c> on
/// macOS (AppKit owns the main thread; see <see cref="IWatchTray.Run"/>). Neither is a GUI
/// framework. Linux has no native implementation yet, and CI must keep today's console loop.
/// </para>
/// <para>
/// The tray is strictly additive. Headless, CI, <c>--no-tray</c>, Linux, and a native startup
/// failure take the no-op path, and <c>watch</c> then behaves as it did before the icon existed.
/// </para>
/// </remarks>
internal static class WatchTray
{
    /// <summary>
    /// Whether this process looks like it could show a tray. GitHub Actions sets <c>CI=true</c>
    /// even on macOS runners; that must not start AppKit. Linux is console-only until a native
    /// status-notifier path exists.
    /// </summary>
    public static bool IsLikelyAvailable()
    {
        if (IsCi()) return false;
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// Try to show a tray. Never throws: a notify-icon failure must not take the watch down
    /// with it, because the console loop is the feature and the icon is a satellite.
    /// </summary>
    public static IWatchTray Create(WatchTrayHooks hooks, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        if (!enabled || !IsLikelyAvailable()) return NullWatchTray.Instance;

        try
        {
            if (OperatingSystem.IsWindows()) return CreateWindows(hooks);
            if (OperatingSystem.IsMacOS()) return CreateMac(hooks);
        }
        catch
        {
            return NullWatchTray.Instance;
        }

        return NullWatchTray.Instance;
    }

    private static bool IsCi()
    {
        var ci = Environment.GetEnvironmentVariable("CI");
        return string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ci, "1", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static IWatchTray CreateWindows(WatchTrayHooks hooks)
        => (IWatchTray?)WindowsWatchTray.TryStart(hooks) ?? NullWatchTray.Instance;

    [SupportedOSPlatform("macos")]
    private static IWatchTray CreateMac(WatchTrayHooks hooks) => new MacWatchTray(hooks);
}

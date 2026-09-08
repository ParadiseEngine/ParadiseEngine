using System.Runtime.Versioning;

namespace Paradise.Cli;

/// <summary>Creates a native watch tray when available, otherwise a no-op.</summary>
/// <remarks>
/// Windows uses a dedicated STA message pump; macOS AppKit owns the main thread.
/// Linux, headless sessions, <c>--no-tray</c> and native startup failures use the console loop.
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

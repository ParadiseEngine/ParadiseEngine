namespace Paradise.Cli;

/// <summary>
/// Tooltip, menu copy, and the macOS menu-bar title for the watch tray. Kept as pure functions
/// so the words can be pinned without standing up a notify icon — each OS rendering (Win32
/// dots, AppKit emoji) is a view of this, not the source of it.
/// </summary>
internal static class WatchPresentation
{
    public static string Tooltip(WatchStatus status, int errorCount) => status switch
    {
        WatchStatus.Alive => "paradise watch — watching",
        WatchStatus.Idle => "paradise watch — idle",
        WatchStatus.Building => "paradise watch — building",
        WatchStatus.Failed => FormatFailed(errorCount),
        _ => "paradise watch",
    };

    /// <summary>
    /// Menu-bar title on macOS. An emoji is enough to glance at; the tooltip carries the words.
    /// </summary>
    public static string MenuBarTitle(WatchStatus status) => status switch
    {
        WatchStatus.Idle => "🟢",
        WatchStatus.Building => "🟡",
        WatchStatus.Failed => "🔴",
        _ => "⚪",
    };

    /// <summary>Disabled menu line that carries the last build's error count, which otherwise
    /// lives as a console line that scrolls past.</summary>
    public static string LastBuildMenu(WatchStatus status, int errorCount) => status switch
    {
        WatchStatus.Alive => "Last build: (none yet)",
        WatchStatus.Building => "Last build: in progress",
        WatchStatus.Idle => "Last build: ok",
        WatchStatus.Failed => errorCount == 1 ? "Last build: 1 error" : $"Last build: {errorCount} errors",
        _ => "Last build: (none yet)",
    };

    private static string FormatFailed(int errorCount) => errorCount == 1
        ? "paradise watch — failed (1 error)"
        : $"paradise watch — failed ({errorCount} errors)";
}

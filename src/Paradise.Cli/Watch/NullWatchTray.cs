namespace Paradise.Cli;

/// <summary>
/// The tray that is not there. One instance is enough: it holds no state, and the watch loop
/// treating "no tray" as this (rather than as <see langword="null"/>) is what keeps every call
/// site branchless.
/// </summary>
internal sealed class NullWatchTray : IWatchTray
{
    public static NullWatchTray Instance { get; } = new();

    private NullWatchTray()
    {
    }

    public bool IsAvailable => false;

    public void SetState(WatchStatus status, int errorCount)
    {
    }

    public void Dispose()
    {
    }
}

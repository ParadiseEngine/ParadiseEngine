namespace Paradise.Cli;

/// <summary>A stateless no-op tray for hosts without a status icon.</summary>
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

    public void Run(Action watch, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(watch);
        watch();
    }

    public void Dispose()
    {
    }
}

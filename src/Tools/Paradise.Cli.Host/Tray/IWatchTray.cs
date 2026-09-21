namespace Paradise.Cli;

/// <summary>A status icon for <c>paradise assets watch</c>, or a no-op when the OS has none.</summary>
internal interface IWatchTray : IDisposable
{
    /// <summary>
    /// Whether an actual icon is showing. False on headless hosts, Linux, when native startup
    /// fails, and <c>--no-tray</c> — the watch loop must not change behaviour based on this
    /// beyond one optional log line.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Update the icon and tooltip. A no-op tray ignores this.</summary>
    void SetState(WatchStatus status, int errorCount);

    /// <summary>
    /// Run the watch loop. Windows pumps on its own STA thread and invokes
    /// <paramref name="watch"/> here; macOS keeps this thread for AppKit and runs the watch
    /// on a worker. The no-op tray just invokes <paramref name="watch"/> on this thread.
    /// </summary>
    void Run(Action watch, Action<string>? log = null);
}

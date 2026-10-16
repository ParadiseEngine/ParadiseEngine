namespace Paradise.Cli;

/// <summary>The watcher's build state displayed by the tray.</summary>
internal enum WatchStatus
{
    /// <summary>The process is up and watching; no rebuild has finished this session yet.</summary>
    Alive,

    /// <summary>Last rebuild succeeded; waiting for the next change.</summary>
    Idle,

    /// <summary>A rebuild is in flight.</summary>
    Building,

    /// <summary>The last rebuild failed; the next attempt transitions to Building.</summary>
    Failed,
}

namespace Paradise.Cli;

/// <summary>
/// What the tray icon is saying. A glance at the icon is supposed to answer "did my save get
/// built", so the four states are the four answers, not a progress bar.
/// </summary>
internal enum WatchStatus
{
    /// <summary>The process is up and watching; no rebuild has finished this session yet.</summary>
    Alive,

    /// <summary>Last rebuild succeeded; waiting for the next change.</summary>
    Idle,

    /// <summary>A rebuild is in flight.</summary>
    Building,

    /// <summary>Last rebuild failed. Stays until a later rebuild succeeds.</summary>
    Failed,
}

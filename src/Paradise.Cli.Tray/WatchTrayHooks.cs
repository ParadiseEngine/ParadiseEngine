namespace Paradise.Cli;

/// <summary>
/// Menu actions the tray can fire. All three land on the watch loop's coordinator, never on the
/// message-pump thread's idea of a rebuild — the pump only signals.
/// </summary>
/// <param name="Stop">End the watch. Same outcome as Ctrl+C.</param>
/// <param name="Rebuild">
/// Kick a rebuild without waiting for a filesystem event. <see langword="null"/> when the watch
/// was started with <c>--no-build</c>, and the menu item is omitted rather than shown disabled:
/// a command that cannot do anything is noise.
/// </param>
/// <param name="OpenOutput">Open the build (or play) folder in the OS file manager.</param>
internal sealed record WatchTrayHooks(Action Stop, Action? Rebuild, Action OpenOutput);

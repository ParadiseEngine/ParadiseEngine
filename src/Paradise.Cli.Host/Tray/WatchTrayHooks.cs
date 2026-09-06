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
/// <param name="Editor">
/// Live play-mode flag. The checkbox reads and flips this; rebuild labels follow so a click
/// cannot look like it ships <c>build/</c> while writing <c>.editor/play</c>.
/// </param>
/// <param name="ToggleEditor">
/// Flip <see cref="Editor"/>. <see langword="null"/> hides the checkbox (tests, the no-op tray).
/// </param>
/// <param name="Game">
/// The game the tray can run, or <see langword="null"/> when the manifest names no
/// <c>[host]</c> scene — the three items are then omitted, not disabled.
/// </param>
internal sealed record WatchTrayHooks(
    Action Stop,
    Action? Rebuild,
    Action OpenOutput,
    WatchToggle Editor,
    Action? ToggleEditor = null,
    WatchTrayGameHooks? Game = null);

/// <summary>Play (plain), Play under <c>dotnet watch</c>, and Stop for the game a watch runs on the manifest's <c>[host]</c> scene; each Play replaces whatever is running. <paramref name="SceneRestart"/> is the checkbox for restarting a watched game when the play tree changes.</summary>
internal sealed record WatchTrayGameHooks(Action Play, Action PlayWatch, Action StopGame, WatchToggle SceneRestart, Action ToggleSceneRestart);

using Paradise.Assets.Pipeline;

namespace Paradise.Cli;

/// <summary>Shared watch-tray labels and status text, independent of native UI.</summary>
internal static class WatchPresentation
{
    /// <summary>Cells in the text progress bar: wide enough to move visibly every few percent,
    /// short enough to leave room for the percentage on a menu line.</summary>
    private const int BarCells = 20;

    /// <summary>The longest asset path a progress line shows; a longer one keeps its end, which
    /// is the part that names the file.</summary>
    private const int CurrentLimit = 48;

    public static string Tooltip(WatchStatus status, int errorCount, WatchProgress? progress = null) => status switch
    {
        WatchStatus.Alive => "paradise watch — watching",
        WatchStatus.Idle => "paradise watch — idle",
        WatchStatus.Building when progress is { } shown => $"paradise watch — building {Percent(shown)}%",
        WatchStatus.Building => "paradise watch — building",
        WatchStatus.Failed => FormatFailed(errorCount),
        _ => "paradise watch",
    };

    /// <summary>Menu-bar title on macOS. An emoji is enough to glance at; the tooltip carries the
    /// words. A running rebuild adds its estimated percentage.</summary>
    public static string MenuBarTitle(WatchStatus status, WatchProgress? progress = null) => status switch
    {
        WatchStatus.Idle => "🟢",
        WatchStatus.Building when progress is { } shown => $"🟡 {Percent(shown)}%",
        WatchStatus.Building => "🟡",
        WatchStatus.Failed => "🔴",
        _ => "⚪",
    };

    /// <summary>Disabled menu line that carries the last build's error count, which otherwise
    /// lives as a console line that scrolls past; while a rebuild runs, its estimated progress
    /// as a bar.</summary>
    public static string LastBuildMenu(WatchStatus status, int errorCount, WatchProgress? progress = null) => status switch
    {
        WatchStatus.Alive => "Last build: (none yet)",
        WatchStatus.Building when progress is { } shown => $"Rebuilding {Bar(shown.Fraction)} {Percent(shown)}%",
        WatchStatus.Building => "Rebuilding…",
        WatchStatus.Idle => "Last build: ok",
        WatchStatus.Failed => errorCount == 1 ? "Last build: 1 error" : $"Last build: {errorCount} errors",
        _ => "Last build: (none yet)",
    };

    /// <summary>Disabled menu line under the bar naming what the rebuild is doing, or null when
    /// no rebuild is reporting (the line is hidden then).</summary>
    public static string? ProgressMenu(WatchStatus status, WatchProgress? progress)
    {
        if (status != WatchStatus.Building || progress is not { } shown) return null;
        return shown.Stage switch
        {
            BuildStage.Sidecars => "Updating sidecars and references",
            BuildStage.Verify => $"Checking {Step(shown)}",
            BuildStage.Assets => $"Importing {Step(shown)}",
            BuildStage.Finish => "Writing the build manifest",
            _ => null,
        };
    }

    /// <summary>Whole percent, and never 100 while the rebuild is still running: a full bar that
    /// keeps saying "building" reads as a hang.</summary>
    private static int Percent(WatchProgress progress) => Math.Min(99, (int)(progress.Fraction * 100));

    private static string Bar(double fraction)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * BarCells);
        return string.Create(BarCells, filled, static (cells, filled) =>
        {
            cells[..filled].Fill('▰');
            cells[filled..].Fill('▱');
        });
    }

    private static string Step(WatchProgress progress)
    {
        var current = progress.Current ?? "";
        if (current.Length > CurrentLimit) current = "…" + current[^(CurrentLimit - 1)..];
        return progress.Total > 0 ? $"{current} ({progress.Done + 1}/{progress.Total})" : current;
    }

    /// <summary>Tray rebuild: play-mode watch rebuilds <c>.editor/play</c>, not <c>build/</c>.</summary>
    public static string RebuildMenu(bool editor) => editor ? "Rebuild play mode" : "Rebuild now";

    /// <summary>Tray open-folder: the tree this watch is actually writing.</summary>
    public static string OpenOutputMenu(bool editor) => editor ? "Open the play folder" : "Open the build folder";

    /// <summary>
    /// Checkbox for <c>--editor</c>: on (the default) rebuilds <c>.editor/play</c> on each
    /// settled change; off rebuilds <c>build/</c>.
    /// </summary>
    public static string EditorToggleMenu => "Play mode";

    /// <summary>Run the game on the manifest's <c>[host]</c> scene from the play tree the watch keeps fresh.</summary>
    public static string PlayMenu => "Play the game";

    /// <summary>The same under <c>dotnet watch</c>: code edits reach the running game, a scene save restarts it.</summary>
    public static string PlayWatchMenu => "Play the game (watch code)";

    public static string StopGameMenu => "Stop the game";

    /// <summary>Checkbox: under "watch code", a play-tree change (a scene save) restarts the game.</summary>
    public static string SceneRestartToggleMenu => "Restart the game on scene save";

    private static string FormatFailed(int errorCount) => errorCount == 1
        ? "paradise watch — failed (1 error)"
        : $"paradise watch — failed ({errorCount} errors)";
}

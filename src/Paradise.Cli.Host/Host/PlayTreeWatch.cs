namespace Paradise.Cli;

/// <summary>
/// Restarts the game under <c>dotnet watch</c> when the play tree changes: an asset build rewrites
/// a couple of hundred files in half a second, so the watch waits for <see cref="Quiet"/> after the
/// last one, kills the game (only the game: <c>dotnet watch</c> stays, parked on "waiting for a
/// file to change"), then touches one file it is known to watch, which is what makes it start the
/// game again — no rebuild, no workspace reload.
/// </summary>
/// <remarks>
/// <c>dotnet watch</c> itself reports a content-only change as "no managed code changes to apply"
/// and keeps the old process running; a scene save would otherwise reach a game that never reads
/// its scene again. The launcher must list the play tree as a <c>Watch</c> item for the touch to
/// be seen.
/// </remarks>
internal sealed class PlayTreeWatch : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    /// <summary>After a restart, the tree is still being touched by the restart itself (the marker) and the game is still starting; a kill in that window lands on a half-started process and leaves dotnet watch in its error state.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(4);

    private readonly FileSystemWatcher _watcher;
    private readonly string _directory;
    private readonly int _watchPid;
    private readonly Action<string> _log;
    private readonly Func<bool> _enabled;
    private readonly Timer _timer;
    // Construction counts as a restart: dotnet watch is starting, and its leaves are MSBuild nodes.
    private long _lastRestartTicks = Environment.TickCount64;

    /// <param name="enabled">Read on every change; the tray's checkbox. Off means a change is ignored, not queued.</param>
    public PlayTreeWatch(string directory, int watchPid, Action<string> log, Func<bool> enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(enabled);

        _directory = directory;
        _watchPid = watchPid;
        _log = log;
        _enabled = enabled;
        _timer = new Timer(_ => Restart(), null, Timeout.Infinite, Timeout.Infinite);
        Directory.CreateDirectory(directory);
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>What the restart touches after the kill; the manifest is in every play tree and named by the launcher's Watch glob.</summary>
    public const string MarkerFileName = "manifest.json";

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_enabled()) return;
        var sinceRestart = Environment.TickCount64 - Interlocked.Read(ref _lastRestartTicks);
        if (sinceRestart < Grace.TotalMilliseconds) return;
        _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Kill the game leaves under the watch, then bump the marker so <c>dotnet watch</c> starts it again.</summary>
    private void Restart()
    {
        if (!ProcessTree.HasGameLeaf(_watchPid))
        {
            // dotnet watch is still building or loading: the leaves are its workers, and killing
            // them fails the build. Nothing is lost either — the game that is about to start
            // reads the tree as it is now.
            return;
        }

        Interlocked.Exchange(ref _lastRestartTicks, Environment.TickCount64);
        _log("play: the play tree changed, restarting the game");
        ProcessTree.KillLeaves(_watchPid);
        // The kill must land before the touch, or dotnet watch sees a change on a running app
        // and applies nothing.
        Thread.Sleep(500);
        var marker = Path.Combine(_directory, MarkerFileName);
        try
        {
            if (File.Exists(marker)) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log($"play: could not touch {marker}: {error.Message}");
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}

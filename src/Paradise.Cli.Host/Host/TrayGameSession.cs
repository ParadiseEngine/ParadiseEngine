using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>Runs one tray-launched game against the watcher's current play tree.</summary>
/// <remarks>Play replaces the game; watch shutdown stops it. Asset builds remain with the watcher's single drainer.</remarks>
internal sealed class TrayGameSession : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly string? _profile;
    private readonly string _scene;
    private readonly IReadOnlyList<IAssetImporter> _importers;
    // One reference swapped atomically: the pair is never observed half-replaced, and there is
    // no lock for a menu click and a watch shutdown to contend on.
    private Running? _running;

    private sealed record Running(CancellationTokenSource Stop, Thread Thread);

    /// <summary>The tray's "restart on scene save" checkbox; on by default, read live by the play-tree watch.</summary>
    public WatchToggle SceneRestart { get; } = new(on: true);

    private TrayGameSession(IFileSystem fileSystem, AssetProjectLayout layout, string? profile, string scene, IReadOnlyList<IAssetImporter> importers)
    {
        _fileSystem = fileSystem;
        _layout = layout;
        _profile = profile;
        _scene = scene;
        _importers = importers;
    }

    /// <summary>The tray's hooks and the session behind them, or <see langword="null"/> when the manifest names no launcher or no scene to play.</summary>
    public static WatchTrayGameHooks? Create(IFileSystem fileSystem, AssetProjectLayout layout, string? profile, IReadOnlyList<IAssetImporter> importers, out TrayGameSession? session)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(importers);

        session = null;
        HostSettings host;
        try
        {
            host = ProjectManifest.Load(fileSystem, layout.Manifest).Host;
        }
        catch (ProjectManifestException)
        {
            return null;
        }

        if (host.Project is null || host.Scene is null) return null;

        var owned = new TrayGameSession(fileSystem, layout, profile, host.Scene, importers);
        session = owned;
        return new WatchTrayGameHooks(
            Play: () => owned.Play(watch: false),
            PlayWatch: () => owned.Play(watch: true),
            StopGame: owned.Stop,
            SceneRestart: owned.SceneRestart,
            ToggleSceneRestart: () => Console.WriteLine(owned.SceneRestart.Toggle()
                ? "watch: a scene save restarts the game"
                : "watch: a scene save leaves the game running"));
    }

    public void Play(bool watch)
    {
        var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                var exit = Verbs.HostPlay(
                    _fileSystem, _layout, _profile,
                    scene: _layout.Assets / _scene,
                    config: null,
                    watch, noBuild: false, noAssets: true,
                    configuration: "Debug",
                    callerArguments: [],
                    _importers,
                    stop.Token,
                    SceneRestart);
                if (!stop.IsCancellationRequested) Console.WriteLine($"watch: the game exited with code {exit}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"watch: the game could not be run: {error.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "paradise-tray-game",
        };

        Release(Interlocked.Exchange(ref _running, new Running(stop, thread)));
        Console.WriteLine(watch ? "watch: playing the game under dotnet watch" : "watch: playing the game");
        thread.Start();
    }

    /// <summary>Cancel and return at once: this runs on the menu thread, and the tree kill that ends the game must not hold AppKit or the Win32 pump hostage.</summary>
    public void Stop() => Release(Interlocked.Exchange(ref _running, null));

    private static void Release(Running? running)
    {
        if (running is null) return;
        running.Stop.Cancel();
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (stop, thread) = ((CancellationTokenSource, Thread))state!;
            thread.Join(TimeSpan.FromSeconds(10));
            stop.Dispose();
        }, (running.Stop, running.Thread));
    }

    /// <summary>The watch is shutting down: wait for the game to be gone, bounded.</summary>
    public void Dispose()
    {
        var running = Interlocked.Exchange(ref _running, null);
        if (running is null) return;
        running.Stop.Cancel();
        running.Thread.Join(TimeSpan.FromSeconds(10));
        running.Stop.Dispose();
    }
}

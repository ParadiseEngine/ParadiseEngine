using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>
/// The game a running <c>paradise assets watch</c> can play from its tray: one at a time, on the
/// manifest's <c>[host]</c> scene, from the play tree the watch itself keeps fresh — so no asset
/// build of its own, since two builders writing one tree at once is what the watch's single
/// drainer rule exists to prevent. A Play replaces the running game; stopping the watch stops it.
/// </summary>
internal sealed class TrayGameSession : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly AssetProjectLayout _layout;
    private readonly string? _profile;
    private readonly string _scene;
    private readonly IReadOnlyList<IAssetImporter> _importers;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Thread? _thread;

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
            StopGame: owned.Stop);
    }

    public void Play(bool watch)
    {
        Stop();
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
                    stop.Token);
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

        lock (_gate)
        {
            _stop = stop;
            _thread = thread;
        }

        Console.WriteLine(watch ? "watch: playing the game under dotnet watch" : "watch: playing the game");
        thread.Start();
    }

    public void Stop()
    {
        CancellationTokenSource? stop;
        Thread? thread;
        lock (_gate)
        {
            stop = _stop;
            thread = _thread;
            _stop = null;
            _thread = null;
        }

        if (stop is null) return;
        stop.Cancel();
        thread?.Join(TimeSpan.FromSeconds(10));
        stop.Dispose();
    }

    public void Dispose() => Stop();
}

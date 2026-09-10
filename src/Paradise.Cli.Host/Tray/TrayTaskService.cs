using Zio;

namespace Paradise.Cli;

/// <summary>Runs project tray tasks beside the asset watcher, with one serialized worker and a shared lifetime.</summary>
internal sealed class TrayTaskService : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly UPath _root;
    private readonly CancellationTokenSource _stop;
    private readonly Func<TrayTaskDefinition, CancellationToken, int> _run;
    private readonly Action<string> _log;
    private readonly (TrayTaskGroup Config, TrayTaskState State)[] _groups;
    private IFileSystemWatcher? _watcher;
    private Task? _worker;

    public TrayTaskService(IFileSystem fileSystem, UPath root, TrayTaskConfiguration config,
        Func<TrayTaskDefinition, CancellationToken, int> run, Action<string> log,
        CancellationToken stopping, Action<string>? open = null)
    {
        _fileSystem = fileSystem;
        _root = root;
        _run = run;
        _log = log;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _groups = config.Groups.Select(group => (group, new TrayTaskState(group.AutoTask,
            group.AutoWatch, TimeSpan.FromMilliseconds(group.DebounceMilliseconds)))).ToArray();
        Menus = _groups.Select(pair => new TrayTaskMenu(pair.Config.Label,
            TrayTaskMenuItem.For(pair.Config, pair.State, pair.Config.OpenDirectory is { } path && open is not null
                ? () => open(path) : null))).ToArray();
    }

    public IReadOnlyList<TrayTaskMenu> Menus { get; }

    public static TrayTaskService Create(IFileSystem fileSystem, UPath root,
        IReadOnlyList<ITrayExtension> extensions, CancellationToken stopping)
    {
        var context = new TrayExtensionContext(fileSystem.ConvertPathToInternal(root),
            new ConsoleProcessRunner(), Console.WriteLine);
        var config = TrayTaskConfiguration.Register(extensions, context, Console.Error.WriteLine);
        return new(fileSystem, root, config,
            (task, stop) => task.Execute(stop).GetAwaiter().GetResult(), Console.WriteLine, stopping,
            path => ShellFolders.Open(fileSystem.ConvertPathToInternal(root / path)));
    }

    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("Tray task service already started.");
        if (_groups.Length == 0) return;
        try
        {
            _watcher = _fileSystem.Watch(_root);
            _watcher.IncludeSubdirectories = true;
            _watcher.Changed += (_, e) => Observe(e.FullPath);
            _watcher.Created += (_, e) => Observe(e.FullPath, _fileSystem.DirectoryExists(e.FullPath));
            _watcher.Deleted += (_, e) => Observe(e.FullPath, structural: true);
            _watcher.Renamed += (_, e) => { Observe(e.OldFullPath, structural: true); Observe(e.FullPath, structural: true); };
            _watcher.Error += (_, e) =>
            {
                _log($"watch: task input watcher failed: {e.Exception.Message}");
                foreach (var group in _groups)
                {
                    if (e.Exception is InternalBufferOverflowException) group.State.Observe(DateTimeOffset.UtcNow);
                    else group.State.DisableAutoWatch(e.Exception.Message);
                }
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
            foreach (var group in _groups)
            {
                group.State.DisableAutoWatch(error.Message);
            }
            _log($"watch: task auto-watch unavailable: {error.Message}; manual tasks remain available");
        }
        _worker = Task.Run(RunAsync);
    }

    public void Observe(UPath path, bool structural = false)
    {
        var prefix = _root.FullName.TrimEnd('/') + "/";
        if (!path.FullName.StartsWith(prefix, StringComparison.Ordinal)) return;
        var relative = path.FullName[prefix.Length..];
        foreach (var group in _groups)
        {
            if (group.Config.Observes(relative, structural)) group.State.Observe(DateTimeOffset.UtcNow);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (var group in _groups)
                {
                    if (_stop.IsCancellationRequested) break;
                    if (group.State.TryBegin(DateTimeOffset.UtcNow) is not { } id) continue;
                    var task = group.Config.Tasks.Single(task => task.Id == id);
                    _log($"watch: {group.Config.Label} — {task.Label}");
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var execution = Task.Run(() => _run(task, cancel.Token));
                    var exitCode = 1;
                    try
                    {
                        while (!execution.IsCompleted)
                        {
                            if (group.State.Snapshot.CancelRequested) cancel.Cancel();
                            await Task.WhenAny(execution, Task.Delay(50)).ConfigureAwait(false);
                        }
                        exitCode = await execution.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { exitCode = 130; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        _log($"watch: {group.Config.Label}: {error.Message}");
                    }
                    finally
                    {
                        group.State.Complete(exitCode);
                        _log($"watch: {group.Config.Label} — {group.State.Snapshot.Status}");
                    }
                }
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void StopAndJoin()
    {
        foreach (var group in _groups) group.State.Stop();
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        StopAndJoin();
        _stop.Dispose();
    }
}

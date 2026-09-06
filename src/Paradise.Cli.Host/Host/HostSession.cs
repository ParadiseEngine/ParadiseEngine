using Zio;

namespace Paradise.Cli;

/// <summary>
/// The launcher's build and run, as the sequence of processes it takes. Every decision — build or
/// not, restore or not, run the dll or hand the project to <c>dotnet watch</c> — is made here
/// against an <see cref="IFileSystem"/> and executed through an <see cref="IProcessRunner"/>, so
/// the sequence is what the tests assert on.
/// </summary>
/// <remarks>
/// The game is a CHILD that this process waits on, not a detached launch: the caller (a terminal,
/// Blender's supervised job) then has one handle whose exit is the game's exit, and whose stop —
/// SIGTERM from Blender, Ctrl+C from a shell — the runner turns into a kill of the whole tree,
/// <c>dotnet watch</c> included.
/// </remarks>
internal sealed class HostSession
{
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _runner;
    private readonly string _dotnet;
    private readonly Action<string> _log;

    public HostSession(IFileSystem fileSystem, IProcessRunner runner, string dotnet, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnet);
        ArgumentNullException.ThrowIfNull(log);

        _fileSystem = fileSystem;
        _runner = runner;
        _dotnet = dotnet;
        _log = log;
    }

    /// <summary><c>dotnet build</c>, quiet: MSBuild's own output is the report when it fails. A success is stamped for the freshness gate.</summary>
    public int Build(UPath csproj, string configuration, bool restore, CancellationToken stop)
    {
        var arguments = new List<string> { "build", Internal(csproj), "-c", configuration, "-v", "q", "--nologo" };
        if (!restore) arguments.Add("--no-restore");
        var exit = _runner.Run(new ProcessSpec(_dotnet, arguments, Internal(csproj.GetDirectory())), stop);
        // A build ended by a stop returns non-zero from the runner; only a clean exit is stamped.
        if (exit == 0) HostFreshness.Stamp(_fileSystem, csproj);
        return exit;
    }

    /// <summary>
    /// Build if stale, then run the built dll with <paramref name="arguments"/> from
    /// <paramref name="workingDirectory"/>; or, with <paramref name="watch"/>, hand the project to
    /// <c>dotnet watch run</c>, which builds, runs, and rebuilds or hot-patches on every source change.
    /// </summary>
    /// <param name="noBuild">Run whatever is built, stale or not — a rebuild is the caller's to refuse, never silently skipped.</param>
    public int Play(
        UPath csproj,
        string configuration,
        UPath workingDirectory,
        IReadOnlyList<string> arguments,
        bool watch,
        bool noBuild,
        CancellationToken stop,
        UPath? restartOnChangesUnder = null,
        Func<bool>? restartEnabled = null)
    {
        var cwd = Internal(workingDirectory);
        if (watch)
        {
            // dotnet watch builds on its own, so the gate only answers whether a restore is owed:
            // a no-op restore over the closure is seconds the window does not need to wait for.
            var watchArguments = new List<string>
            {
                "watch", "run", "--non-interactive", "--project", Internal(csproj), "-c", configuration,
            };
            if (!HostFreshness.Inspect(_fileSystem, csproj, configuration).NeedsRestore) watchArguments.Add("--no-restore");
            watchArguments.Add("--");
            watchArguments.AddRange(arguments);
            _log($"play: dotnet watch run on {Internal(csproj)} (a source change hot-patches or restarts the game)");
            if (restartOnChangesUnder is not { } tree) return _runner.Run(new ProcessSpec(_dotnet, watchArguments, cwd), stop);

            var treeDirectory = Internal(tree);
            PlayTreeWatch? playTree = null;
            try
            {
                return _runner.Run(
                    new ProcessSpec(_dotnet, watchArguments, cwd),
                    stop,
                    started: pid => playTree = new PlayTreeWatch(treeDirectory, pid, _log, restartEnabled ?? (static () => true)));
            }
            finally
            {
                playTree?.Dispose();
            }
        }

        var freshness = HostFreshness.Inspect(_fileSystem, csproj, configuration);
        if (!freshness.IsFresh)
        {
            if (noBuild)
            {
                _log("play: --no-build, running a launcher that is out of date");
            }
            else
            {
                _log(freshness.OutputExists
                    ? $"play: sources changed since the last build, building{(freshness.NeedsRestore ? " (with restore)" : string.Empty)}"
                    : "play: no launcher built yet, building");
                var built = Build(csproj, configuration, freshness.NeedsRestore, stop);
                if (built != 0) return built;
                if (stop.IsCancellationRequested) return ConsoleProcessRunner.Interrupted;
                freshness = HostFreshness.Inspect(_fileSystem, csproj, configuration);
            }
        }

        if (freshness.Output is not { } output || !_fileSystem.FileExists(output))
        {
            _log($"play: the build produced no {freshness.Output?.GetName() ?? "output"} under {Internal(csproj.GetDirectory())}/bin");
            return 1;
        }

        var runArguments = new List<string> { Internal(output) };
        runArguments.AddRange(arguments);
        return _runner.Run(new ProcessSpec(_dotnet, runArguments, cwd), stop);
    }

    private string Internal(UPath path) => _fileSystem.ConvertPathToInternal(path);
}

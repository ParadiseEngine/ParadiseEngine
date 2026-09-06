using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

/// <summary>The sequence of processes a Play is, asserted through a recording runner.</summary>
public class HostSessionTests
{
    private static readonly UPath s_csproj = "/repo/Game.Launcher/Game.Launcher.csproj";
    private static readonly UPath s_output = "/repo/Game.Launcher/bin/Debug/net10.0/Game.Launcher.dll";
    private static readonly UPath s_stampFile = "/repo/Game.Launcher/obj/paradise-host.stamp";
    private static readonly DateTime s_stamp = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly Func<ProcessSpec, int> _exit;

        public RecordingRunner(Func<ProcessSpec, int>? exit = null) => _exit = exit ?? (static _ => 0);

        public List<ProcessSpec> Specs { get; } = [];

        public int Run(ProcessSpec spec, CancellationToken stop, Action<int>? started = null)
        {
            Specs.Add(spec);
            started?.Invoke(4242);
            return _exit(spec);
        }
    }

    /// <summary>A memory filesystem whose internal paths are its own: the session renders every path through the mount, and a Physical one would refuse a test path.</summary>
    private sealed class TransparentFileSystem : MemoryFileSystem
    {
        protected override string ConvertPathToInternalImpl(UPath path) => path.FullName;

        protected override UPath ConvertPathFromInternalImpl(string innerPath) => innerPath;
    }

    private static TransparentFileSystem Tree(bool built = true, bool restored = true)
    {
        var fileSystem = new TransparentFileSystem();
        fileSystem.CreateDirectory("/repo/Game.Launcher/obj");
        fileSystem.WriteAllText(s_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fileSystem.SetLastWriteTime(s_csproj, s_stamp);
        fileSystem.WriteAllText("/repo/Game.Launcher/Program.cs", "class P {}");
        fileSystem.SetLastWriteTime("/repo/Game.Launcher/Program.cs", s_stamp);
        if (restored)
        {
            fileSystem.WriteAllText("/repo/Game.Launcher/obj/project.assets.json", """{ "libraries": {}, "project": { "frameworks": { "net10.0": {} } } }""");
            fileSystem.SetLastWriteTime("/repo/Game.Launcher/obj/project.assets.json", s_stamp.AddMinutes(1));
        }

        if (built)
        {
            fileSystem.CreateDirectory(s_output.GetDirectory());
            fileSystem.WriteAllText(s_output, "MZ");
            fileSystem.SetLastWriteTime(s_output, s_stamp.AddMinutes(2));
            fileSystem.WriteAllText(s_stampFile, "");
            fileSystem.SetLastWriteTime(s_stampFile, s_stamp.AddMinutes(2));
        }

        return fileSystem;
    }

    private static HostSession Session(IFileSystem fileSystem, RecordingRunner runner, List<string>? log = null) =>
        new(fileSystem, runner, "/sdk/dotnet", (log ?? []).Add);

    [Test]
    public async Task a_fresh_launcher_runs_without_a_build()
    {
        using var fileSystem = Tree();
        var runner = new RecordingRunner();

        var exit = Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", ["--scene", "/repo/.editor/play/levels/a.prefab"], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs).Count().IsEqualTo(1);
        await Assert.That(runner.Specs[0].FileName).IsEqualTo("/sdk/dotnet");
        await Assert.That(runner.Specs[0].Arguments).IsEquivalentTo(new[] { s_output.FullName, "--scene", "/repo/.editor/play/levels/a.prefab" });
        await Assert.That(runner.Specs[0].WorkingDirectory).IsEqualTo("/repo");
    }

    [Test]
    public async Task a_stale_launcher_is_built_without_restore_and_then_run()
    {
        using var fileSystem = Tree();
        fileSystem.SetLastWriteTime("/repo/Game.Launcher/Program.cs", s_stamp.AddMinutes(3));
        var runner = new RecordingRunner();

        var exit = Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs).Count().IsEqualTo(2);
        await Assert.That(runner.Specs[0].Arguments).IsEquivalentTo(new[] { "build", s_csproj.FullName, "-c", "Debug", "-v", "q", "--nologo", "--no-restore" });
        await Assert.That(runner.Specs[1].Arguments).IsEquivalentTo(new[] { s_output.FullName });
        // The stamp the session wrote is what makes the next Play skip the build.
        await Assert.That(HostFreshness.Inspect(fileSystem, s_csproj, "Debug").IsFresh).IsTrue();
    }

    [Test]
    public async Task a_never_restored_launcher_is_built_with_restore()
    {
        using var fileSystem = Tree(built: false, restored: false);
        var runner = new RecordingRunner(spec =>
        {
            if (spec.Arguments[0] != "build") return 0;
            fileSystem.WriteAllText("/repo/Game.Launcher/obj/project.assets.json", """{ "libraries": {}, "project": { "frameworks": { "net10.0": {} } } }""");
            fileSystem.CreateDirectory(s_output.GetDirectory());
            fileSystem.WriteAllText(s_output, "MZ");
            return 0;
        });

        var exit = Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs[0].Arguments).DoesNotContain("--no-restore");
        await Assert.That(runner.Specs[1].Arguments[0]).IsEqualTo(s_output.FullName);
    }

    [Test]
    public async Task a_failed_build_stops_the_play_with_its_exit_code()
    {
        using var fileSystem = Tree();
        fileSystem.SetLastWriteTime("/repo/Game.Launcher/Program.cs", s_stamp.AddMinutes(3));
        var runner = new RecordingRunner(static spec => spec.Arguments[0] == "build" ? 1 : 0);

        var exit = Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(runner.Specs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task no_build_runs_a_stale_launcher_and_says_so()
    {
        using var fileSystem = Tree();
        fileSystem.SetLastWriteTime("/repo/Game.Launcher/Program.cs", s_stamp.AddMinutes(3));
        var runner = new RecordingRunner();
        var log = new List<string>();

        var exit = Session(fileSystem, runner, log).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: true, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs).Count().IsEqualTo(1);
        await Assert.That(runner.Specs[0].Arguments[0]).IsEqualTo(s_output.FullName);
        await Assert.That(log).Contains("play: --no-build, running a launcher that is out of date");
    }

    [Test]
    public async Task a_missing_output_after_a_clean_build_is_reported_not_run()
    {
        using var fileSystem = Tree(built: false);
        var runner = new RecordingRunner();
        var log = new List<string>();

        var exit = Session(fileSystem, runner, log).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(runner.Specs).Count().IsEqualTo(1);
        await Assert.That(log.Last()).Contains("Game.Launcher.dll");
    }

    [Test]
    public async Task watch_hands_the_project_to_dotnet_watch_run_without_a_build_of_its_own()
    {
        // dotnet watch builds on its own; a build here would race the one it is about to start.
        // A never-restored tree keeps the restore.
        using var fileSystem = Tree(built: false, restored: false);
        var runner = new RecordingRunner();

        var exit = Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", ["--scene", "x"], watch: true, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs).Count().IsEqualTo(1);
        await Assert.That(runner.Specs[0].Arguments).IsEquivalentTo(new[]
        {
            "watch", "run", "--non-interactive", "--project", s_csproj.FullName, "-c", "Debug", "--", "--scene", "x",
        });
        await Assert.That(runner.Specs[0].WorkingDirectory).IsEqualTo("/repo");
    }

    [Test]
    public async Task watch_skips_the_restore_when_no_project_file_changed()
    {
        using var fileSystem = Tree();
        var runner = new RecordingRunner();

        Session(fileSystem, runner).Play(s_csproj, "Debug", "/repo", [], watch: true, noBuild: false, CancellationToken.None);

        await Assert.That(runner.Specs[0].Arguments).IsEquivalentTo(new[]
        {
            "watch", "run", "--non-interactive", "--project", s_csproj.FullName, "-c", "Debug", "--no-restore", "--",
        });
    }

    [Test]
    public async Task a_multi_targeted_launcher_is_refused_rather_than_guessed_at()
    {
        using var fileSystem = Tree();
        fileSystem.WriteAllText("/repo/Game.Launcher/obj/project.assets.json", """{ "libraries": {}, "project": { "frameworks": { "net10.0": {}, "net10.0-windows": {} } } }""");
        var runner = new RecordingRunner();
        var log = new List<string>();

        var exit = Session(fileSystem, runner, log).Play(s_csproj, "Debug", "/repo", [], watch: false, noBuild: false, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(runner.Specs).IsEmpty();
        await Assert.That(log.Last()).Contains("net10.0 and net10.0-windows");
    }

    [Test]
    public async Task build_restores_by_default_and_runs_from_the_project_directory()
    {
        using var fileSystem = Tree();
        var runner = new RecordingRunner();

        var exit = Session(fileSystem, runner).Build(s_csproj, "Release", restore: true, CancellationToken.None);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Specs[0].Arguments).IsEquivalentTo(new[] { "build", s_csproj.FullName, "-c", "Release", "-v", "q", "--nologo" });
        await Assert.That(runner.Specs[0].WorkingDirectory).IsEqualTo("/repo/Game.Launcher");
    }
}

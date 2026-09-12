using System.Runtime.Loader;

using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class TrayExtensionLoaderTests
{
    private sealed class ProcessRunner(Func<ProcessSpec, int> run) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public int Run(ProcessSpec spec, CancellationToken stop, Action<int>? started = null)
        {
            Calls.Add(spec);
            return run(spec);
        }
    }

    private static int PublishFixture(ProcessSpec spec)
    {
        var output = spec.Arguments[5];
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "plugin-fixture")))
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
        return 0;
    }

    private sealed class Context : ITrayExtensionContext
    {
        public string ProjectDirectory => "/project";
        public List<string> Messages { get; } = [];
        public List<string> Arguments { get; } = [];
        public Task<int> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Arguments.Add(executable);
            Arguments.AddRange(arguments);
            return Task.FromResult(cancellationToken.IsCancellationRequested ? 130 : 0);
        }
        public void Log(string message) => Messages.Add(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tray-dll-" + Guid.NewGuid().ToString("N"));
        public PhysicalFileSystem FileSystem { get; } = new();
        public AssetProjectLayout Layout { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "assets"));
            Directory.CreateDirectory(Path.Combine(Root, "extensions"));
            var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(Root, "extensions", Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(Root, "assets", "project.toml"), """
                name = "plugin-test"
                schema_version = 1
                [extensions]
                assemblies = ["extensions/Paradise.Cli.Test.Plugin.dll"]
                """);
            Layout = AssetProjectLayout.Locate(FileSystem, FileSystem.ConvertPathFromInternal(Root));
        }
        public void Dispose()
        {
            FileSystem.Dispose();
            // Windows keeps noncollectible DLLs mapped until process exit. Do not mask a test
            // result with a cleanup refusal; restart-based loading intentionally has this limit.
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public string PublishedDirectory => Path.Combine(Root, ".editor", "extensions");

        public void DeclareProject()
        {
            Directory.CreateDirectory(Path.Combine(Root, "tools with spaces"));
            File.WriteAllText(Path.Combine(Root, "tools with spaces", "Paradise.Cli.Test.Plugin.csproj"), "<Project />");
            Directory.CreateDirectory(PublishedDirectory);
            foreach (var file in Directory.GetFiles(Path.Combine(Root, "extensions")))
                File.Copy(file, Path.Combine(PublishedDirectory, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(Root, "assets", "project.toml"), """
                name = "plugin-test"
                schema_version = 1
                [extensions]
                projects = ["tools with spaces/Paradise.Cli.Test.Plugin.csproj"]
                """);
        }

    }

    [Test]
    public async Task missing_dll_is_published_before_discovery_with_separate_arguments()
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        var dll = Path.Combine(fixture.PublishedDirectory, "Paradise.Cli.Test.Plugin.dll");
        File.Delete(dll);
        var runner = new ProcessRunner(PublishFixture);
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, _ => { }, processes: runner);
        await Assert.That(extensions.TrayExtensions.Any(extension => extension.GetType().Name == "GoodExtension")).IsTrue();
        var spec = runner.Calls.Single();
        await Assert.That(spec.FileName).IsEqualTo("dotnet");
        var root = ProjectPaths.Internal(fixture.FileSystem, fixture.Layout.Root);
        await Assert.That(spec.WorkingDirectory).IsEqualTo(root);
        await Assert.That(spec.Arguments.SequenceEqual(new[]
        {
            "publish", Path.Combine(root, "tools with spaces", "Paradise.Cli.Test.Plugin.csproj"), "--configuration", "Release",
            "--output", spec.Arguments[5], "--nologo", "--disable-build-servers",
            "-p:PublishAot=false", "-p:PublishTrimmed=false",
        })).IsTrue();
        await Assert.That(Path.GetDirectoryName(spec.Arguments[5])).IsEqualTo(Path.Combine(root, ".editor", "extensions"));
        await Assert.That(Directory.Exists(spec.Arguments[5])).IsFalse();
    }

    [Test]
    public async Task an_existing_dll_is_built_again_before_each_load()
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        using var another = new Fixture();
        another.DeclareProject();
        var runner = new ProcessRunner(PublishFixture);
        using var first = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, _ => { }, processes: runner);
        using var second = ExtensionLoader.Load(another.FileSystem, another.Layout, [], true, _ => { }, processes: runner);
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
        await Assert.That(second.TrayExtensions).IsNotEmpty();
    }

    [Test]
    [Arguments(1)]
    [Arguments(130)]
    public async Task failed_or_interrupted_publish_never_loads_the_old_dll(int exitCode)
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, errors.Add,
            processes: new ProcessRunner(_ => exitCode));
        await Assert.That(extensions.TrayExtensions).IsEmpty();
        await Assert.That(errors.Single()).Contains($"exit {exitCode}");
        await Assert.That(extensions.BuildExitCode).IsEqualTo(exitCode);
    }

    [Test]
    public async Task missing_project_never_loads_the_old_dll()
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        File.Delete(Path.Combine(fixture.Root, "tools with spaces", "Paradise.Cli.Test.Plugin.csproj"));
        var runner = new ProcessRunner(_ => throw new InvalidOperationException("Unexpected publish"));
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, errors.Add, processes: runner);
        await Assert.That(extensions.TrayExtensions).IsEmpty();
        await Assert.That(runner.Calls).IsEmpty();
        await Assert.That(errors.Single()).Contains("missing project");
    }

    [Test]
    public async Task successful_publish_with_wrong_assembly_name_never_loads_the_old_dll()
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, errors.Add,
            processes: new ProcessRunner(_ => 0));
        await Assert.That(extensions.TrayExtensions).IsEmpty();
        await Assert.That(extensions.BuildExitCode).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("AssemblyName");
    }

    [Test]
    public async Task all_projects_finish_publishing_before_any_dll_is_loaded()
    {
        using var fixture = new Fixture();
        fixture.DeclareProject();
        File.WriteAllText(Path.Combine(fixture.Root, "Second.csproj"), "<Project />");
        var manifest = Path.Combine(fixture.Root, "assets", "project.toml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("projects = [", "projects = [\"Second.csproj\", ", StringComparison.Ordinal));
        var runner = new ProcessRunner(spec =>
        {
            if (!spec.Arguments[1].EndsWith("Second.csproj", StringComparison.Ordinal)) return 1;
            PublishFixture(spec);
            File.Move(Path.Combine(spec.Arguments[5], "Paradise.Cli.Test.Plugin.dll"), Path.Combine(spec.Arguments[5], "Second.dll"));
            return 0;
        });
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, _ => { }, processes: runner);
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
        await Assert.That(extensions.TrayExtensions).IsEmpty();
        await Assert.That(extensions.BuildExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task dry_run_and_prebuilt_assemblies_do_not_invoke_dotnet()
    {
        using var fixture = new Fixture();
        var runner = new ProcessRunner(_ => throw new InvalidOperationException("Unexpected publish"));
        using var prebuilt = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, _ => { }, processes: runner);
        fixture.DeclareProject();
        using var dry = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], false, _ => { }, buildProjects: false, processes: runner);
        await Assert.That(runner.Calls).IsEmpty();
        await Assert.That(prebuilt.TrayExtensions).IsNotEmpty();
    }

    [Test]
    public async Task dll_discovery_shares_contracts_resolves_dependencies_and_invokes_csharp_actions()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, AssetImporters.All, true, errors.Add);
        var context = new Context();
        var groups = TrayTaskConfiguration.Register(extensions.TrayExtensions, context, errors.Add).Groups;
        await Assert.That(groups.Single().Label).IsEqualTo("DLL dependency loaded");
        await Assert.That(extensions.TrayExtensions.Count).IsEqualTo(3);
        await Assert.That(extensions.Importers[^1].Name).IsEqualTo("fixture-importer");
        var dual = extensions.TrayExtensions.Single(extension => extension.GetType().Name == "DualRoleExtension");
        await Assert.That(ReferenceEquals(dual, extensions.Importers[^1])).IsTrue();
        await Assert.That(AssemblyLoadContext.GetLoadContext(dual.GetType().Assembly) == AssemblyLoadContext.Default).IsFalse();
        await Assert.That(await groups[0].Tasks[0].Execute(CancellationToken.None).ConfigureAwait(false)).IsEqualTo(0);
        await Assert.That(context.Arguments.SequenceEqual(["fixture-compiler", "source with spaces", "--check"])).IsTrue();
        await Assert.That(errors.Any(error => error.Contains("constructor failure", StringComparison.Ordinal))).IsTrue();
        await Assert.That(errors.Any(error => error.Contains("registration failure", StringComparison.Ordinal))).IsTrue();
        await Assert.That(errors.Any(error => error.Contains("no public IAssetImporter", StringComparison.Ordinal))).IsFalse();
        extensions.Dispose();
        extensions.Dispose();
        await Assert.That(context.Messages.Count(message => message == "fixture disposed")).IsEqualTo(1);
        await Assert.That(context.Messages.Count(message => message == "dual role disposed")).IsEqualTo(1);
    }

    [Test]
    public async Task non_watch_commands_keep_importers_without_constructing_tray_only_extensions()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, AssetImporters.All, false, errors.Add);
        await Assert.That(extensions.TrayExtensions).IsEmpty();
        await Assert.That(extensions.Importers[^1].Name).IsEqualTo("fixture-importer");
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task bad_and_missing_assemblies_do_not_prevent_loading_a_valid_plugin()
    {
        using var fixture = new Fixture();
        var manifest = Path.Combine(fixture.Root, "assets", "project.toml");
        File.WriteAllText(Path.Combine(fixture.Root, "extensions", "broken.dll"), "not an assembly");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(
            "assemblies = [", "assemblies = [\"extensions/missing.dll\", \"extensions/broken.dll\", ", StringComparison.Ordinal));
        var errors = new List<string>();
        using var extensions = ExtensionLoader.Load(fixture.FileSystem, fixture.Layout, [], true, errors.Add);
        await Assert.That(extensions.TrayExtensions.Any(extension => extension.GetType().Name == "GoodExtension")).IsTrue();
        await Assert.That(errors.Any(error => error.Contains("missing.dll", StringComparison.Ordinal))).IsTrue();
        await Assert.That(errors.Any(error => error.Contains("broken.dll", StringComparison.Ordinal))).IsTrue();
    }
}

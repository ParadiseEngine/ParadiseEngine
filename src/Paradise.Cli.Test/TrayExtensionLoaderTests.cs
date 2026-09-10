using System.Runtime.Loader;

using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class TrayExtensionLoaderTests
{
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

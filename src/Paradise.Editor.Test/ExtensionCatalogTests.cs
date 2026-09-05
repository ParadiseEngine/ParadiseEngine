using Paradise.Diagnostics;
using Paradise.Editor.Core;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.ImGui;
using Paradise.Editor.ImGui.Shell;
using Zio.FileSystems;

namespace Paradise.Editor.Test;

/// <summary>Loading an extension out of an assembly this project cannot see at compile time.</summary>
/// <remarks>
/// <para>
/// Paradise.Editor.TestPlugin is referenced with <c>ReferenceOutputAssembly=false</c>, so its types
/// are genuinely unavailable here — everything below arrives through <see cref="ExtensionCatalog"/>
/// or not at all. That is the only arrangement that can tell a working loader from a compile-time
/// reference wearing one's clothes.
/// </para>
/// <para>
/// The assertion that matters most is <see cref="a_plugin_panel_registers_through_the_normal_path"/>:
/// it proves the plugin's <c>IShellExtension</c> is the SAME type as the host's. Get the assembly
/// context wrong and it is a different type with the same name, the cast fails, and the error reads
/// like nonsense.
/// </para>
/// </remarks>
public class ExtensionCatalogTests
{
    private const string PluginPanelId = "sample.window.notes";
    private const string PluginOwnerId = "sample.plugin";

    private static string PluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    [Test]
    public async Task an_extension_is_found_in_an_assembly_this_project_cannot_reference()
    {
        using var catalog = ExtensionCatalog.Discover(PluginDirectory);

        await Assert.That(catalog.Extensions.Select(extension => extension.Id)).Contains(PluginOwnerId);
    }

    [Test]
    public async Task a_plugin_panel_registers_through_the_normal_path()
    {
        using var catalog = ExtensionCatalog.Discover(PluginDirectory);
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        var registries = new EditorRegistries();
        var dispatcher = new OperatorDispatcher(session, registries.Operators);
        using var layout = new EditorLayout();
        var shell = new EditorShell(dispatcher, registries, layout);

        foreach (var extension in catalog.Extensions) shell.Register(extension, registries);

        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == PluginPanelId)).IsTrue();
        // AddPanel ran inside the plugin, so it got the toggle and the View entry too.
        await Assert.That(dispatcher.Find($"{PluginPanelId}.toggle")).IsNotNull();
        await Assert.That(registries.Menus.Entries.Any(e => e.Menu == "View" && e.OperatorId == $"{PluginPanelId}.toggle")).IsTrue();

        // …and it unloads the same way a built-in does.
        shell.Unregister(PluginOwnerId, registries);
        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == PluginPanelId)).IsFalse();
    }

    // A type that implements the interface but cannot be constructed is reported, not thrown, and
    // must not take the working extension in the same assembly down with it.
    [Test]
    public async Task an_unconstructable_extension_is_reported_and_the_rest_still_load()
    {
        using var catalog = ExtensionCatalog.Discover(PluginDirectory);

        await Assert.That(catalog.Problems.Any(problem => problem.Contains("NeedsArguments", StringComparison.Ordinal))).IsTrue();
        await Assert.That(catalog.Extensions.Select(extension => extension.Id)).Contains(PluginOwnerId);
    }

    // Having no extensions is the normal case, not an error.
    [Test]
    public async Task a_missing_directory_yields_an_empty_catalog()
    {
        using var catalog = ExtensionCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "no-such-folder"));

        await Assert.That(catalog.Extensions).IsEmpty();
        await Assert.That(catalog.Problems).IsEmpty();
    }

    // An editor that refuses to start because somebody left a stale file in the folder is worse
    // than one that starts and says so.
    [Test]
    public async Task a_file_that_is_not_an_assembly_is_reported_rather_than_fatal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"paradise-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "not-an-assembly.dll"), "this is not a PE file");

            using var catalog = ExtensionCatalog.Discover(directory, new CollectingLogger());

            await Assert.That(catalog.Extensions).IsEmpty();
            await Assert.That(catalog.Problems.Single()).Contains("not-an-assembly.dll");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // AssemblyLoadContext refuses a relative path outright, and a relative --extensions is the
    // obvious thing to type. Before this was resolved, it threw ArgumentException past the catch
    // and took the editor down on startup.
    [Test]
    public async Task a_relative_directory_loads_the_same_extensions()
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), PluginDirectory);

        using var catalog = ExtensionCatalog.Discover(relative);

        await Assert.That(catalog.Extensions.Select(extension => extension.Id)).Contains(PluginOwnerId);
    }

    [Test]
    public async Task what_was_loaded_is_reported()
    {
        var log = new CollectingLogger();

        using var catalog = ExtensionCatalog.Discover(PluginDirectory, log);

        await Assert.That(log.Messages.Any(message => message.Contains("extension(s)", StringComparison.Ordinal))).IsTrue();
    }
}

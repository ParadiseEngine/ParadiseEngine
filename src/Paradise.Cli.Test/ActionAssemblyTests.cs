using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Zio;
using Zio.FileSystems;

namespace Paradise.Cli.Test;

public class ActionAssemblyTests
{
    private const string Component = "489d02da-c275-4f8d-b24d-835ac3ed0d97";

    [Test]
    public async Task declared_toggle_receives_context_and_returns_generic_editor_updates()
    {
        using var fixture = new Fixture();
        fixture.FileSystem.WriteAllText(fixture.State, "{\"AutoUpdate\":true}");
        var errors = new List<string>();
        var result = ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "Visible", Guid.Empty, errors.Add,
            value: true, state: fixture.State, response: fixture.Response);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(errors).IsEmpty();
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("Visible").GetBoolean()).IsTrue();
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("SawState").GetBoolean()).IsTrue();
        await Assert.That(response.RootElement.GetProperty("documentChanged").GetBoolean()).IsTrue();
        await Assert.That(response.RootElement.GetProperty("overlays")[0].GetProperty("vertices").GetArrayLength()).IsEqualTo(9);
    }

    [Test]
    public async Task declared_save_hook_receives_save_flag_and_the_action_decides_enabled_state()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "Bake", null, errors.Add, onSave: true,
            response: fixture.Response)).IsEqualTo(0);
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("SawSave").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task a_save_hook_without_an_inspector_action_runs_only_post_save()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "SaveOnly", null, errors.Add, onSave: true,
            response: fixture.Response)).IsEqualTo(0);
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("SawSaveHook").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task an_on_save_toggle_receives_its_stored_value()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "AutoUpdate", null, errors.Add,
            value: true, onSave: true, response: fixture.Response)).IsEqualTo(0);
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("SawAutoUpdate").GetBoolean()).IsTrue();
    }

    [Test]
    [Arguments("Helper", false, null)]
    [Arguments("Generic", false, null)]
    [Arguments("Instance", false, null)]
    [Arguments("Ref", false, null)]
    [Arguments("Overloaded", false, null)]
    [Arguments("Async", false, null)]
    [Arguments("ReturnsValue", false, null)]
    [Arguments("Visible", false, null)]
    [Arguments("Bake", false, true)]
    [Arguments("Simple", true, null)]
    [Arguments("SaveOnly", false, null)]
    [Arguments("AutoUpdate", true, null)]
    public async Task malformed_or_undeclared_invocations_do_not_publish_a_response(string action, bool onSave, bool? value)
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), action, null, errors.Add, value, onSave,
            response: fixture.Response)).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsFalse();
    }

    [Test]
    public async Task cli_invokes_an_explicit_built_assembly_and_parses_toggle_protocol()
    {
        using var fixture = new Fixture();
        var exit = BuildHost.Run(["assets", "invoke-action", fixture.Path(fixture.Document), Component,
            "Visible", "--project", fixture.Root, "--no-build", "--assembly", fixture.Path(fixture.Assembly),
            "--value", "false", "--response", fixture.Path(fixture.Response)]);
        await Assert.That(exit).IsEqualTo(0);
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("Visible").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task malformed_state_is_rejected_before_action_effects()
    {
        using var fixture = new Fixture();
        fixture.FileSystem.WriteAllText(fixture.State, "{\"Visible\":\"yes\"}");
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "Bake", null, errors.Add,
            state: fixture.State, response: fixture.Response)).IsEqualTo(1);
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsFalse();
    }

    [Test]
    public async Task action_failure_reports_the_cause_without_returning_partial_updates()
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "Fails", null, errors.Add,
            response: fixture.Response)).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("deliberate action failure");
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsFalse();
    }

    [Test]
    public async Task newer_engine_reference_reports_the_version_requirement()
    {
        var requested = new AssemblyName("Paradise.Authoring, Version=999.0.0.0");
        var available = typeof(AuthorActionContext).Assembly.GetName();
        var failure = await Assert.That(() => ActionAssembly.ValidateEngineVersion(requested, available))
            .Throws<FileLoadException>();
        await Assert.That(failure!.Message).Contains("upgrade the paradise tool to match");
        await Assert.That(failure.Message).Contains("999.0.0.0");
    }

    [Test]
    [Arguments("--value")]
    [Arguments("--response")]
    [Arguments("--state")]
    [Arguments("--assembly")]
    public async Task incomplete_cli_options_are_usage_errors(string option)
    {
        await Assert.That(BuildHost.Run(["assets", "invoke-action", "level.prefab", Component, "Visible", option]))
            .IsEqualTo(2);
    }

    [Test]
    public async Task an_explicit_assembly_requires_no_build()
    {
        await Assert.That(BuildHost.Run(["assets", "invoke-action", "level.prefab", Component, "Visible",
            "--assembly", "game.dll"])).IsEqualTo(2);
    }

    [Test]
    [Arguments("Simple", null)]
    [Arguments("ToggleSimple", true)]
    [Arguments("ToggleSimple", false)]
    public async Task contextless_actions_are_supported(string action, bool? value)
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), action, null, errors.Add, value,
            response: fixture.Response)).IsEqualTo(0);
        await Assert.That(errors).IsEmpty();
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsTrue();
    }

    [Test]
    public async Task external_action_uses_the_same_dependency_identity_as_engine_apis()
    {
        using var fixture = new Fixture();
        var assembly = fixture.FileSystem.ConvertPathFromInternal(System.IO.Path.Combine(
            AppContext.BaseDirectory, "plugin-fixture", "Paradise.Cli.Test.Plugin.dll"));
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, assembly,
            fixture.Document, Guid.Parse("2cba26a4-38f5-4e27-8cf9-8018f94e4eca"), "SharedDependency", null,
            errors.Add, response: fixture.Response)).IsEqualTo(0);
        await Assert.That(errors).IsEmpty();
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("toggles").GetProperty("SharedDependency").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task duplicate_component_ids_are_refused_before_any_action_runs()
    {
        using var fixture = new Fixture();
        var assembly = fixture.FileSystem.ConvertPathFromInternal(System.IO.Path.Combine(
            AppContext.BaseDirectory, "plugin-fixture", "Paradise.Cli.Test.Plugin.dll"));
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, assembly,
            fixture.Document, Guid.Parse("7e6a8f9e-077d-4325-980c-ec1a78f82312"), "Run", null,
            errors.Add, response: fixture.Response)).IsEqualTo(1);
        await Assert.That(errors.Single()).Contains("ambiguous");
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsFalse();
    }

    [Test]
    [Arguments("Surface")]
    [Arguments("ContextSurface")]
    [Arguments("EmptySurface")]
    public async Task previews_return_geometry_without_toggle_or_document_updates(string action)
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), action, null, errors.Add,
            response: fixture.Response)).IsEqualTo(0);

        await Assert.That(errors).IsEmpty();
        using var response = JsonDocument.Parse(fixture.FileSystem.ReadAllText(fixture.Response));
        await Assert.That(response.RootElement.GetProperty("overlays").GetArrayLength()).IsEqualTo(1);
        await Assert.That(response.RootElement.GetProperty("overlays")[0].GetProperty("id").GetString()).IsEqualTo("surface");
        await Assert.That(response.RootElement.GetProperty("overlays")[0].GetProperty("visible").GetBoolean()).IsTrue();
        await Assert.That(response.RootElement.GetProperty("toggles").EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(response.RootElement.GetProperty("documentChanged").GetBoolean()).IsFalse();
    }

    [Test]
    [Arguments("NullSurface")]
    [Arguments("NullableSurface")]
    [Arguments("MalformedVertices")]
    [Arguments("InfiniteVertices")]
    [Arguments("IncompleteIndices")]
    [Arguments("OutOfBoundsIndices")]
    [Arguments("NegativeIndices")]
    [Arguments("MalformedColor")]
    [Arguments("EmptyId")]
    [Arguments("MutatingSurface")]
    [Arguments("MutatingToggles")]
    [Arguments("ExtraOverlays")]
    public async Task malformed_preview_results_do_not_replace_the_previous_response(string action)
    {
        using var fixture = new Fixture();
        fixture.FileSystem.WriteAllText(fixture.Response, "previous response");
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), action, null, errors.Add,
            response: fixture.Response)).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(fixture.FileSystem.ReadAllText(fixture.Response)).IsEqualTo("previous response");
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, null)]
    public async Task preview_actions_refuse_toggle_values_and_save_invocations(bool onSave, bool? value)
    {
        using var fixture = new Fixture();
        var errors = new List<string>();
        await Assert.That(ActionAssembly.Invoke(fixture.FileSystem, fixture.Layout, fixture.Assembly,
            fixture.Document, Guid.Parse(Component), "Surface", null, errors.Add, value, onSave,
            response: fixture.Response)).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(fixture.FileSystem.FileExists(fixture.Response)).IsFalse();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"paradise-action-{Guid.NewGuid():N}");
        public PhysicalFileSystem FileSystem { get; } = new();
        public AssetProjectLayout Layout { get; }
        public UPath Document => Layout.Assets / "level.prefab";
        public UPath State => Layout.Editor / "state.json";
        public UPath Response => Layout.Editor / "response.json";
        public UPath Assembly => FileSystem.ConvertPathFromInternal(typeof(ActionAssemblyTests).Assembly.Location);
        public string Path(UPath path) => FileSystem.ConvertPathToInternal(path);
        public Fixture()
        {
            Layout = new AssetProjectLayout(FileSystem.ConvertPathFromInternal(Root));
            FileSystem.CreateDirectory(Layout.Assets);
            FileSystem.CreateDirectory(Layout.Editor);
            FileSystem.WriteAllText(Layout.Manifest, "name = \"actions\"\nschema_version = 1\n");
            FileSystem.WriteAllText(Document, "# action fixture");
        }
        public void Dispose()
        {
            FileSystem.DeleteDirectory(Layout.Root, true);
            FileSystem.Dispose();
        }
    }

    [Authored, Guid(Component)]
    public sealed record ActionFixture
    {
        [AuthoredToggle]
        public static void Visible(AuthorActionContext context, bool value)
        {
            if (context.Value != value) throw new InvalidOperationException("toggle argument disagrees with context");
            context.Result.Toggles["SawState"] = context.ToggleValues.GetValueOrDefault("AutoUpdate");
            context.Result.DocumentChanged = true;
            context.Result.Overlays.Add(new AuthorActionOverlay
            {
                Id = "preview", Visible = value, Vertices = [0, 0, 0, 1, 0, 0, 0, 0, 1], Indices = [0, 1, 2],
            });
        }
        [AuthoredButton, AuthoredOnSave]
        public static void Bake(AuthorActionContext context) => context.Result.Toggles["SawSave"] = context.IsSave;
        [AuthoredOnSave]
        public static void SaveOnly(AuthorActionContext context) => context.Result.Toggles["SawSaveHook"] = context.IsSave;
        [AuthoredToggle, AuthoredOnSave]
        public static void AutoUpdate(AuthorActionContext context, bool value)
            => context.Result.Toggles["SawAutoUpdate"] = context.IsSave && value;
        [AuthoredButton] public static void Simple() { }
        [AuthoredToggle] public static void ToggleSimple(bool value) { }
        public static void Helper() => throw new InvalidOperationException("must not run");
        [AuthoredButton] public static void Generic<T>() { }
        [AuthoredButton] public void Instance() { }
        [AuthoredButton] public static void Ref(ref AuthorActionContext context) { }
        [AuthoredButton] public static int ReturnsValue() => 1;
        [AuthoredButton] public static void Overloaded() { }
        public static void Overloaded(int value) { }
        [AuthoredButton] public static async void Async() { await Task.Yield(); }
        [AuthoredButton] public static void Fails(AuthorActionContext context)
        {
            context.Result.DocumentChanged = true;
            throw new InvalidOperationException("deliberate action failure");
        }
        [AuthoredPreview] public static AuthorActionOverlay Surface() => new()
        {
            Id = "surface", Vertices = [0, 0, 0, 1, 0, 0, 0, 0, 1], Indices = [0, 1, 2],
        };
        [AuthoredPreview] public static AuthorActionOverlay ContextSurface(AuthorActionContext context)
        {
            if (context.Value is not null || context.IsSave) throw new InvalidOperationException("wrong preview context");
            return Surface();
        }
        [AuthoredPreview] public static AuthorActionOverlay EmptySurface() => new() { Id = "surface" };
        [AuthoredPreview] public static AuthorActionOverlay NullSurface() => null!;
        [AuthoredPreview] public static AuthorActionOverlay? NullableSurface() => Surface();
        [AuthoredPreview] public static AuthorActionOverlay MalformedVertices() => Surface() with { Vertices = [0, 1] };
        [AuthoredPreview] public static AuthorActionOverlay InfiniteVertices() => Surface() with { Vertices = [0, 1, float.NaN] };
        [AuthoredPreview] public static AuthorActionOverlay IncompleteIndices() => Surface() with { Indices = [0, 1] };
        [AuthoredPreview] public static AuthorActionOverlay OutOfBoundsIndices() => Surface() with { Indices = [0, 1, 3] };
        [AuthoredPreview] public static AuthorActionOverlay NegativeIndices() => Surface() with { Indices = [0, 1, -1] };
        [AuthoredPreview] public static AuthorActionOverlay MalformedColor() => Surface() with { Color = [0, 1, 2, 3] };
        [AuthoredPreview] public static AuthorActionOverlay EmptyId() => Surface() with { Id = "" };
        [AuthoredPreview] public static AuthorActionOverlay MutatingSurface(AuthorActionContext context)
        {
            context.Result.DocumentChanged = true;
            return Surface();
        }
        [AuthoredPreview] public static AuthorActionOverlay MutatingToggles(AuthorActionContext context)
        {
            context.Result.Toggles["Visible"] = true;
            return Surface();
        }
        [AuthoredPreview] public static AuthorActionOverlay ExtraOverlays(AuthorActionContext context)
        {
            context.Result.Overlays.Add(Surface());
            return Surface();
        }
    }
}

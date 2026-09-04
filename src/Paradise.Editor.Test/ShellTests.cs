using Hexa.NET.ImGui;
using Paradise.Editor.Core;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui.Panels;
using Paradise.Editor.ImGui.Shell;
using Zio.FileSystems;
using Paradise.Editor.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Test;

/// <summary>The dock recipe, as a node graph rather than a screenshot.</summary>
/// <remarks>Worth asserting because the failure is silent: ImGui builds the same nodes whether or
/// not a window id in the recipe matches a window anybody submits, so a typo produces a correct
/// layout containing nothing and shows up only when the panel is written.</remarks>
[NotInParallel]
public class ShellTests
{
    private static uint DockOf(string windowId)
    {
        ImGuiApi.Begin(EditorDockspace.LabelFor(windowId).Insert(0, "Panel"));
        var node = ImGuiApi.GetWindowDockID();
        ImGuiApi.End();
        return node;
    }

    [Test]
    public async Task the_default_recipe_puts_every_panel_in_the_arrangement_it_describes()
    {
        using var context = new EditorImGuiContext();
        using var layout = new EditorLayout();
        var docks = new Dictionary<string, uint>();

        // Twice: the recipe runs on the first frame, and a window only reports its node once it
        // has been submitted into the node the recipe put it in.
        for (var frame = 0; frame < 2; frame++)
        {
            context.Frame(() =>
            {
                layout.Draw();
                foreach (var id in new[]
                {
                    EditorWindows.Hierarchy, EditorWindows.Inspector, EditorWindows.Assets,
                    EditorWindows.Console, EditorWindows.Scene, EditorWindows.Stats,
                })
                {
                    docks[id] = DockOf(id);
                }
            });
        }

        foreach (var (id, node) in docks)
        {
            await Assert.That(node).IsNotEqualTo(0u).Because($"'{id}' was never docked");
        }

        // Assets, Console and Stats share one node — that is what a tab bar is.
        await Assert.That(docks[EditorWindows.Console]).IsEqualTo(docks[EditorWindows.Assets]);
        await Assert.That(docks[EditorWindows.Stats]).IsEqualTo(docks[EditorWindows.Assets]);

        // The other three are each somewhere else.
        var distinct = new[]
        {
            docks[EditorWindows.Hierarchy], docks[EditorWindows.Inspector],
            docks[EditorWindows.Scene], docks[EditorWindows.Assets],
        };
        await Assert.That(distinct.Distinct().Count()).IsEqualTo(4);
    }

    // ImGui hashes a window's id from the part after ###, so a recipe written against the visible
    // title would lose every panel's position the day somebody renames or localises one.
    [Test]
    public async Task a_panel_keeps_its_place_when_its_title_changes()
    {
        using var context = new EditorImGuiContext();
        using var layout = new EditorLayout();
        uint before = 0;
        uint after = 0;

        context.Frame(() => { layout.Draw(); ImGuiApi.Begin(EditorDockspace.LabelFor(EditorWindows.Scene).Insert(0, "Scene")); ImGuiApi.End(); });
        context.Frame(() =>
        {
            layout.Draw();
            ImGuiApi.Begin($"Scene{EditorDockspace.LabelFor(EditorWindows.Scene)}");
            before = ImGuiApi.GetWindowDockID();
            ImGuiApi.End();
            ImGuiApi.Begin($"Scène (renommée){EditorDockspace.LabelFor(EditorWindows.Scene)}");
            after = ImGuiApi.GetWindowDockID();
            ImGuiApi.End();
        });

        await Assert.That(before).IsNotEqualTo(0u);
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task a_workspace_id_cannot_be_registered_twice()
    {
        using var layout = new EditorLayout();

        await Assert.That(() => layout.Add(EditorLayout.Default)).Throws<InvalidOperationException>();
        await Assert.That(() => layout.SwitchTo("editor.workspace.nothing")).Throws<InvalidOperationException>();
    }

    // Seed 0 means the id is a pure function of the name, so two dockspaces sharing one silently
    // share a node — "my two workspaces are the same workspace". Debug-only, hence the guard.
    [Test]
    public async Task two_dockspaces_may_not_share_a_name()
    {
        using (var first = new EditorDockspace("duplicate-name-probe"))
        {
            await Assert.That(() => new EditorDockspace("duplicate-name-probe")).Throws<InvalidOperationException>();
        }

        // Released with the first, so the name is free again.
        using var reused = new EditorDockspace("duplicate-name-probe");
        await Assert.That(reused.NodeId).IsNotEqualTo(0u);
    }
}

/// <summary>The shell composed the way a host composes it, so the wiring between registries,
/// operators and panels is exercised rather than assumed.</summary>
[NotInParallel]
public class ShellWiringTests
{
    private sealed record Composed(
        EditorShell Shell,
        EditorLayout Layout,
        EditorRegistries Registries,
        OperatorDispatcher Dispatcher) : IDisposable
    {
        public void Dispose() => Layout.Dispose();
    }

    private static Composed Compose()
    {
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        var registries = new EditorRegistries();
        var dispatcher = new OperatorDispatcher(session, registries.Operators);
        var layout = new EditorLayout();
        var shell = new EditorShell(dispatcher, registries, layout);
        foreach (var extension in EditorExtensions.BuiltIn) shell.Register(extension, registries);
        return new Composed(shell, layout, registries, dispatcher);
    }

    // The bug this is here for: a panel's close box sets a flag, and before the toggle operator
    // existed nothing cleared it — the panel was gone until the editor restarted.
    [Test]
    public async Task a_closed_panel_can_be_reopened()
    {
        using var composed = Compose();
        var panel = composed.Shell.Windows.Entries.First(window => window.Descriptor.Id == EditorWindows.Hierarchy);
        var toggle = $"{EditorWindows.Hierarchy}.toggle";

        await Assert.That(panel.IsOpen).IsTrue();
        await Assert.That(composed.Dispatcher.IsChecked(toggle)).IsTrue();

        panel.IsOpen = false; // what the X does
        await Assert.That(composed.Dispatcher.IsChecked(toggle)).IsFalse();

        composed.Dispatcher.Dispatch(toggle, OperatorArgs.None);
        await Assert.That(panel.IsOpen).IsTrue();
    }

    // Every panel, not just the one that was reported — a panel with no way back is the same bug
    // whichever panel it is.
    [Test]
    public async Task every_panel_has_a_toggle_in_the_view_menu()
    {
        using var composed = Compose();
        var viewLabels = composed.Registries.Menus.Entries
            .Where(entry => entry.Menu == "View")
            .Select(entry => entry.OperatorId)
            .ToHashSet();

        foreach (var panel in composed.Shell.Windows.Entries)
        {
            var toggle = $"{panel.Descriptor.Id}.toggle";
            await Assert.That(composed.Dispatcher.Find(toggle)).IsNotNull()
                .Because($"'{panel.Descriptor.Id}' has no toggle operator");
            await Assert.That(viewLabels).Contains(toggle)
                .Because($"'{panel.Descriptor.Id}' has no View menu entry");
        }
    }

    // Reachable from the palette too, which is the point of everything being an operator: a panel
    // toggle nobody put in a menu is still findable by typing its name.
    [Test]
    public async Task panel_toggles_are_reachable_by_name_from_the_palette()
    {
        using var composed = Compose();
        var matched = composed.Registries.Operators.Entries
            .Where(candidate => FuzzyMatch.TryScore("hier", candidate.Label, out _))
            .Select(candidate => candidate.Id)
            .ToArray();

        await Assert.That(matched).Contains($"{EditorWindows.Hierarchy}.toggle");
    }

    // An ordinary command must not draw an empty tick box beside it; only a toggle reports state.
    [Test]
    public async Task an_ordinary_command_reports_no_checked_state()
    {
        using var composed = Compose();

        await Assert.That(composed.Dispatcher.IsChecked(ResetLayoutOperator.OperatorId)).IsNull();
        await Assert.That(composed.Dispatcher.IsChecked(UndoOperator.OperatorId)).IsNull();
    }

    [Test]
    public async Task the_active_workspace_is_ticked_and_cannot_be_switched_to()
    {
        using var composed = Compose();
        var active = $"{composed.Layout.ActiveId}.activate";

        await Assert.That(composed.Dispatcher.IsChecked(active)).IsTrue();
        await Assert.That(composed.Dispatcher.IsAvailable(active)).IsFalse();
    }
}


/// <summary>What a third party gets: they reference the two packages, implement IShellExtension,
/// and build their own editor. There is no runtime loading — the editor publishes ahead-of-time and
/// NativeAOT has no JIT to compile a plugin assembly — so this IS the extension story, and it has to
/// work through exactly the door the built-in shell uses.</summary>
[NotInParallel]
public class ShellExtensibilityTests
{
    private const string PanelId = "vendor.window.profiler";

    private sealed class VendorPanel() : EditorWindow(new WindowDescriptor(PanelId, "Profiler", DockArea.Bottom, "Vendor"))
    {
        public int Frames { get; private set; }

        protected override void DrawContent() => Frames++;
    }

    private sealed class VendorExtension : IShellExtension
    {
        public const string OwnerId = "vendor.tools";

        public string Id => OwnerId;

        public void Register(ShellRegistrar registrar) => registrar
            .AddPanel(new VendorPanel())
            .AddOperator(new VendorOperator())
            .AddFieldRenderer(new FieldRenderer("vendor.duration", _ => { }));
    }

    private sealed class VendorOperator : IOperator
    {
        public string Id => "vendor.profile.start";
        public string Label => "Start profiling";
        public string Description => "Stands in for anything a third party would add.";
        public bool IsAvailable(IOperatorContext context) => true;
        public OperatorResult Execute(IOperatorContext context, OperatorArgs args) => OperatorResult.Finished;
    }

    private static (EditorShell Shell, EditorLayout Layout, EditorRegistries Registries, OperatorDispatcher Dispatcher) Compose()
    {
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        var registries = new EditorRegistries();
        var dispatcher = new OperatorDispatcher(session, registries.Operators);
        var layout = new EditorLayout();
        var shell = new EditorShell(dispatcher, registries, layout);
        foreach (var extension in EditorExtensions.BuiltIn) shell.Register(extension, registries);
        shell.Register(new VendorExtension(), registries);
        return (shell, layout, registries, dispatcher);
    }

    [Test]
    public async Task an_extension_adds_a_panel_that_draws_and_can_be_reopened()
    {
        using var context = new EditorImGuiContext();
        var (shell, layout, registries, dispatcher) = Compose();
        using var _ = layout;

        var panel = shell.Windows.Entries.Single(window => window.Descriptor.Id == PanelId);
        var toggle = $"{PanelId}.toggle";

        // AddPanel did all four steps, which is the point of it existing.
        await Assert.That(registries.Windows.Entries.Any(w => w.Id == PanelId)).IsTrue();
        await Assert.That(dispatcher.Find(toggle)).IsNotNull();
        await Assert.That(registries.Menus.Entries.Any(e => e.Menu == "View" && e.OperatorId == toggle)).IsTrue();

        panel.IsOpen = false;
        dispatcher.Dispatch(toggle, OperatorArgs.None);
        await Assert.That(panel.IsOpen).IsTrue();
    }

    // Owner scoping is what makes an extension removable, and the state it owns is split across
    // three registries in two assemblies. ONE call has to clear all of them, or an unload that
    // looks complete leaves panels drawing over an editor that has forgotten about them — or, more
    // quietly, leaves an inspector row still overriding a built-in one.
    [Test]
    public async Task unregistering_removes_everything_the_extension_added()
    {
        var (shell, layout, registries, dispatcher) = Compose();
        using var _ = layout;

        await Assert.That(shell.FieldRenderers.For("vendor.duration")).IsNotNull();

        shell.Unregister(VendorExtension.OwnerId, registries);

        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == PanelId)).IsFalse();
        await Assert.That(dispatcher.Find("vendor.profile.start")).IsNull();
        await Assert.That(dispatcher.Find($"{PanelId}.toggle")).IsNull();
        await Assert.That(registries.Menus.Entries.Any(e => e.OperatorId == $"{PanelId}.toggle")).IsFalse();
        await Assert.That(registries.Windows.Entries.Any(w => w.Id == PanelId)).IsFalse();
        await Assert.That(shell.FieldRenderers.For("vendor.duration")).IsNull();

        // and the built-in shell is untouched
        await Assert.That(dispatcher.Find(UndoOperator.OperatorId)).IsNotNull();
        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == EditorWindows.Hierarchy)).IsTrue();
    }

    // The whole point of registering at runtime is doing it again afterwards: an extension that
    // cannot be re-added after an unload is a one-shot, not a toggle.
    [Test]
    public async Task an_extension_can_be_registered_again_after_being_unregistered()
    {
        var (shell, layout, registries, dispatcher) = Compose();
        using var _ = layout;

        shell.Unregister(VendorExtension.OwnerId, registries);
        shell.Register(new VendorExtension(), registries);

        await Assert.That(shell.Windows.Entries.Count(w => w.Descriptor.Id == PanelId)).IsEqualTo(1);
        await Assert.That(dispatcher.Find($"{PanelId}.toggle")).IsNotNull();
        await Assert.That(registries.Menus.Entries.Count(e => e.OperatorId == $"{PanelId}.toggle")).IsEqualTo(1);
    }

    // Unregistering from inside a frame is the realistic case — an operator the extension itself
    // registered, run from a menu. Every registry hands out a snapshot, so the in-flight draw
    // finishes over the array it started on rather than throwing.
    [Test]
    public async Task an_extension_can_unregister_itself_mid_frame()
    {
        using var context = new EditorImGuiContext();
        var (shell, layout, registries, _) = Compose();
        using var __ = layout;

        context.Frame(() =>
        {
            shell.Layout.Draw();
            foreach (var window in shell.Windows.Entries)
            {
                window.Draw();
                if (window.Descriptor.Id == PanelId) shell.Unregister(VendorExtension.OwnerId, registries);
            }
        });

        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == PanelId)).IsFalse();
    }

    // A vendor panel is as reachable as a built-in one — same palette, same fuzzy search.
    [Test]
    public async Task an_extension_operator_is_reachable_from_the_palette()
    {
        var (_, layout, registries, _) = Compose();
        using var _unused = layout;

        var matched = registries.Operators.Entries
            .Where(candidate => FuzzyMatch.TryScore("prof", candidate.Label, out _))
            .Select(candidate => candidate.Id)
            .ToArray();

        await Assert.That(matched).Contains("vendor.profile.start");
        await Assert.That(matched).Contains($"{PanelId}.toggle");
    }

    // Panels from two extensions interleave in registration order rather than both starting at the
    // same View-menu slot.
    [Test]
    public async Task panel_menu_slots_do_not_collide_between_extensions()
    {
        var (_, layout, registries, _) = Compose();
        using var _unused = layout;

        var panelOrders = registries.Menus.Entries
            .Where(entry => entry.Menu == "View" && entry.OperatorId.EndsWith(".toggle", StringComparison.Ordinal))
            .Select(entry => entry.Order)
            .ToArray();

        await Assert.That(panelOrders.Distinct().Count()).IsEqualTo(panelOrders.Length);
    }
}


/// <summary>Every built-in view is an extension in its own right.</summary>
/// <remarks>The claim in #222 is that built-ins register the way an external extension would. With
/// all six panels inside one extension that was true once; with one extension each it is true six
/// times, and — the part that has teeth — a host can drop a single panel without dropping the
/// shell.</remarks>
[NotInParallel]
public class BuiltInPanelExtensionTests
{
    private static (EditorShell Shell, EditorLayout Layout, EditorRegistries Registries, OperatorDispatcher Dispatcher) Compose()
    {
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        var registries = new EditorRegistries();
        var dispatcher = new OperatorDispatcher(session, registries.Operators);
        var layout = new EditorLayout();
        var shell = new EditorShell(dispatcher, registries, layout);
        foreach (var extension in EditorExtensions.BuiltIn) shell.Register(extension, registries);
        return (shell, layout, registries, dispatcher);
    }

    [Test]
    public async Task the_shell_extension_contributes_no_panels_of_its_own()
    {
        var registries = new EditorRegistries();
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        using var layout = new EditorLayout();
        var shell = new EditorShell(new OperatorDispatcher(session, registries.Operators), registries, layout);

        shell.Register(new ShellExtension(), registries);

        await Assert.That(shell.Windows.Entries).IsEmpty();
        // …but its chrome is there, so the split did not take the shell with it.
        await Assert.That(registries.Operators.Entries.Any(op => op.Id == UndoOperator.OperatorId)).IsTrue();
    }

    [Test]
    public async Task every_built_in_panel_owns_a_distinct_token()
    {
        var (shell, layout, _, _) = Compose();
        using var _unused = layout;

        var owners = EditorExtensions.BuiltIn.Select(extension => extension.Id).ToArray();
        await Assert.That(owners.Distinct().Count()).IsEqualTo(owners.Length);
        await Assert.That(shell.Windows.Entries.Count).IsEqualTo(6);
    }

    // The point of the split: dropping one panel leaves the other five and the shell alone.
    [Test]
    public async Task one_panel_can_be_dropped_without_touching_the_others()
    {
        var (shell, layout, registries, dispatcher) = Compose();
        using var _unused = layout;

        shell.Unregister(AssetsExtension.OwnerId, registries);

        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == EditorWindows.Assets)).IsFalse();
        await Assert.That(dispatcher.Find($"{EditorWindows.Assets}.toggle")).IsNull();
        await Assert.That(shell.Windows.Entries.Count).IsEqualTo(5);
        await Assert.That(dispatcher.Find(UndoOperator.OperatorId)).IsNotNull();
        await Assert.That(dispatcher.Find(ResetLayoutOperator.OperatorId)).IsNotNull();
        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == EditorWindows.Console)).IsTrue();
    }

    // A host composing its own set — an in-game editor has no use for an asset browser — should
    // not have to register a panel and then remove it.
    [Test]
    public async Task a_host_can_compose_a_subset_up_front()
    {
        var registries = new EditorRegistries();
        var session = new EditorSession(new InMemorySceneProvider(), new MemoryFileSystem());
        using var layout = new EditorLayout();
        var shell = new EditorShell(new OperatorDispatcher(session, registries.Operators), registries, layout);

        foreach (var extension in EditorExtensions.BuiltIn.Where(e => e.Id != AssetsExtension.OwnerId))
        {
            shell.Register(extension, registries);
        }

        await Assert.That(shell.Windows.Entries.Count).IsEqualTo(5);
        await Assert.That(shell.Windows.Entries.Any(w => w.Descriptor.Id == EditorWindows.Assets)).IsFalse();
    }
}

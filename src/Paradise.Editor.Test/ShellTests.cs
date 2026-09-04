using Hexa.NET.ImGui;
using Paradise.Editor.Core;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
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
        new ShellExtension(shell).Register(new EditorRegistrar(registries, new OwnerToken(ShellExtension.OwnerId)));
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

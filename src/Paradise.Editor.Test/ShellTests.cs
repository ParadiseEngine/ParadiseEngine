using Hexa.NET.ImGui;
using Paradise.Editor.Core.Shell;
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

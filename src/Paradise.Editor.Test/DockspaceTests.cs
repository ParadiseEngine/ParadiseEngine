using Hexa.NET.ImGui;
using Paradise.Editor.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Test;

/// <summary>The dockspace, headless. Every panel the editor grows docks into this node, so "the
/// node exists and the seed ran into it" is the property E1's layout recipe is built on.</summary>
[NotInParallel]
public class DockspaceTests
{
    private const string PanelTitle = "Panel";

    private static void DrawPanel()
    {
        ImGuiApi.Begin(PanelTitle);
        ImGuiApi.End();
    }

    [Test]
    public async Task the_dockspace_builds_a_node_and_the_seed_docks_a_window_into_it()
    {
        using var context = new EditorImGuiContext();
        var seeds = 0;
        uint dockedInto = 0;
        using var dockspace = new EditorDockspace("TestDockspace", root =>
        {
            seeds++;
            EditorDockspace.Dock(PanelTitle, root);
        });

        context.Frame(() =>
        {
            dockspace.Draw();
            ImGuiApi.Begin(PanelTitle);
            dockedInto = ImGuiApi.GetWindowDockID();
            ImGuiApi.End();
        });

        await Assert.That(seeds).IsEqualTo(1);
        await Assert.That(dockspace.HasNode).IsTrue();
        await Assert.That(dockspace.NodeSize.X).IsGreaterThan(0f);
        await Assert.That(dockedInto).IsEqualTo(dockspace.NodeId);
    }

    // The seed is what a fresh profile gets. Running it again would discard the arrangement a
    // user made — and would do it silently, every frame, so nothing they dragged would stick.
    [Test]
    public async Task an_existing_node_is_not_re_seeded()
    {
        using var context = new EditorImGuiContext();
        var seeds = 0;
        using var dockspace = new EditorDockspace("TestDockspace", _ => seeds++);

        for (var frame = 0; frame < 4; frame++) context.Frame(() => { dockspace.Draw(); DrawPanel(); });

        await Assert.That(seeds).IsEqualTo(1);
        await Assert.That(dockspace.HasNode).IsTrue();
    }

    [Test]
    public async Task reset_layout_re_seeds_once()
    {
        using var context = new EditorImGuiContext();
        var seeds = 0;
        using var dockspace = new EditorDockspace("TestDockspace", _ => seeds++);

        context.Frame(() => { dockspace.Draw(); DrawPanel(); });
        dockspace.ResetLayout();
        context.Frame(() => { dockspace.Draw(); DrawPanel(); });
        context.Frame(() => { dockspace.Draw(); DrawPanel(); });

        await Assert.That(seeds).IsEqualTo(2);
    }

    // GetID hashes against the CURRENT WINDOW's id stack, so an id taken that way changes if the
    // dockspace is ever drawn inside a Begin/End and cannot be computed between frames at all.
    // Both were real: the second one segfaulted the host's headless smoke run.
    [Test]
    public async Task the_node_id_is_stable_and_readable_outside_a_frame()
    {
        using var context = new EditorImGuiContext();
        using var dockspace = new EditorDockspace("TestDockspace");
        var before = dockspace.NodeId;

        await Assert.That(dockspace.HasNode).IsFalse();
        context.Frame(dockspace.Draw);

        await Assert.That(dockspace.NodeId).IsEqualTo(before);
        await Assert.That(dockspace.HasNode).IsTrue();
    }

    [Test]
    public async Task a_seeded_split_puts_the_two_windows_in_different_nodes()
    {
        using var context = new EditorImGuiContext();
        uint left = 0;
        uint centre = 0;
        using var dockspace = new EditorDockspace("TestDockspace", root =>
        {
            var side = EditorDockspace.Split(ref root, ImGuiDir.Left, 0.25f);
            EditorDockspace.Dock("Left", side);
            EditorDockspace.Dock("Centre", root);
        });

        context.Frame(() =>
        {
            dockspace.Draw();
            ImGuiApi.Begin("Left");
            left = ImGuiApi.GetWindowDockID();
            ImGuiApi.End();
            ImGuiApi.Begin("Centre");
            centre = ImGuiApi.GetWindowDockID();
            ImGuiApi.End();
        });

        await Assert.That(left).IsNotEqualTo(0u);
        await Assert.That(centre).IsNotEqualTo(0u);
        await Assert.That(left).IsNotEqualTo(centre);
    }
}

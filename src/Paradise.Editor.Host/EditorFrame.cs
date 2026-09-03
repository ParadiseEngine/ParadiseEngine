using Paradise.Editor.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Host;

/// <summary>What the editor draws each frame.</summary>
/// <remarks>
/// <para>
/// One type for both run modes so the windowed run and the headless capture cannot drift: a
/// screenshot that proves nothing about what a person sees is worse than no screenshot.
/// </para>
/// <para>
/// E0 draws the dockspace and ImGui's own demo window docked into it. The demo is a deliberate
/// choice of subject, not a placeholder to delete later: it exercises far more of ImGui's widget
/// surface — tables, plots, trees, popups, text input — than any panel written by hand at this
/// stage would, so the capture is a real test of the texture protocol and the renderer. E1
/// replaces the seed recipe and adds the shell around it.
/// </para>
/// </remarks>
internal sealed class EditorFrame
{
    private const string DemoWindowTitle = "Dear ImGui Demo";

    private readonly EditorDockspace _dockspace = new(
        "ParadiseEditorDockspace",
        root => EditorDockspace.Dock(DemoWindowTitle, root));

    private bool _showDemo = true;

    public EditorDockspace Dockspace => _dockspace;

    public void Draw()
    {
        _dockspace.Draw();
        if (_showDemo) ImGuiApi.ShowDemoWindow(ref _showDemo);
    }
}

using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.TestPlugin;

/// <summary>What a third party ships: a panel, contributed by an extension in its own assembly.
/// </summary>
public sealed class SamplePluginExtension : IShellExtension
{
    public const string OwnerId = "sample.plugin";
    public const string PanelId = "sample.window.notes";

    public string Id => OwnerId;

    public void Register(ShellRegistrar registrar) =>
        registrar.AddPanel(new NotesPanel());
}

public sealed class NotesPanel() : EditorWindow(new WindowDescriptor(
    SamplePluginExtension.PanelId, "Notes", DockArea.Right, "Plugin"))
{
    protected override void DrawContent()
    {
    }
}

/// <summary>Deliberately unusable: it implements the interface but cannot be constructed without
/// arguments, so the loader must report it and carry on rather than throwing.</summary>
public sealed class NeedsArgumentsExtension(string name) : IShellExtension
{
    public string Id => name;

    public void Register(ShellRegistrar registrar)
    {
    }
}

using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.ImGui.Panels;

/// <summary>The Console panel, contributed the way any extension contributes one.</summary>
/// <remarks>
/// <para>
/// What the editor and the asset pipeline reported, filtered by severity.
/// </para>
/// <para>
/// Its OWN extension, not a line in the shell's, so it owns a token of its own: a host that does
/// not want it calls <c>EditorShell.Unregister("{OwnerId}", registries)</c> and gets rid of the
/// panel, its toggle and its View entry together. Sharing the shell's token would make that
/// all-or-nothing, and would make "built-ins register the way an extension would" true once rather
/// than once per panel.
/// </para>
/// <para>
/// E2 replaces the placeholder with the real drawing and adds this panel's own operators and
/// inspector rows here, where they will be removed with it.
/// </para>
/// </remarks>
public sealed class ConsoleExtension : IShellExtension
{
    public const string OwnerId = "editor.panel.console";

    public static WindowDescriptor Descriptor { get; } =
        new(EditorWindows.Console, $"{EditorIcons.Terminal} Console", DockArea.Bottom, "Project");

    public string Id => OwnerId;

    public void Register(ShellRegistrar registrar) =>
        registrar.AddPanel(new PlaceholderPanel(Descriptor));
}

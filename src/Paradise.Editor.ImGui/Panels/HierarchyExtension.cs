using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.ImGui.Panels;

/// <summary>The Hierarchy panel, contributed the way any extension contributes one.</summary>
/// <remarks>
/// <para>
/// The scene's objects as a tree, from each object's meta.Parent.
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
public sealed class HierarchyExtension : IShellExtension
{
    public const string OwnerId = "editor.panel.hierarchy";

    public static WindowDescriptor Descriptor { get; } =
        new(EditorWindows.Hierarchy, $"{EditorIcons.AccountTree} Hierarchy", DockArea.Left, "Scene");

    public string Id => OwnerId;

    public void Register(ShellRegistrar registrar) =>
        registrar.AddPanel(new PlaceholderPanel(Descriptor));
}

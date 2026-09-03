using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Input;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>Everything the built-in shell contributes, registered through the same registrar an
/// external extension would use.</summary>
/// <remarks>This is the claim in #222 that "built-in panels register through the same path an
/// external extension would" being kept honest rather than asserted: if the shell needed a private
/// route to add a menu item or a keybinding, so would everyone else.</remarks>
public sealed class ShellExtension(EditorShell shell) : IEditorExtension
{
    public const string OwnerId = "editor.shell";

    public string Id => OwnerId;

    public void Register(EditorRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        registrar
            .AddOperator(new UndoOperator())
            .AddOperator(new RedoOperator())
            .AddOperator(new ResetLayoutOperator(shell.Layout))
            .AddOperator(new OpenPaletteOperator(shell.Palette));

        foreach (var window in Windows)
        {
            registrar.AddWindow(window);
            shell.Windows.Add(registrar.Owner, new PlaceholderPanel(window));
        }
        foreach (var workspace in shell.Layout.Workspaces)
        {
            registrar.AddWorkspace(new WorkspaceDescriptor(workspace.Id, workspace.Title));
        }

        registrar
            .AddMenuEntry(new MenuEntry("Edit", "Undo", UndoOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("Edit", "Redo", RedoOperator.OperatorId, 1))
            .AddMenuEntry(new MenuEntry("View", "Command palette", OpenPaletteOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, 1))
            .AddMenuEntry(new MenuEntry("View", "Reset layout", ResetLayoutOperator.OperatorId, 2));

        Bind(registrar, "Ctrl+Z", UndoOperator.OperatorId);
        Bind(registrar, "Ctrl+Shift+Z", RedoOperator.OperatorId);
        Bind(registrar, "Ctrl+Shift+P", OpenPaletteOperator.OperatorId);
    }

    /// <summary>The panels the shell knows about. They have no drawing yet — E2 brings that — but
    /// the descriptors exist now because the dock recipe positions them by id, and a recipe
    /// referring to windows nothing declares is a layout nobody can check.</summary>
    private static IEnumerable<WindowDescriptor> Windows =>
    [
        new(EditorWindows.Hierarchy, $"{EditorIcons.AccountTree} Hierarchy", DockArea.Left, "Scene"),
        new(EditorWindows.Inspector, $"{EditorIcons.Tune} Inspector", DockArea.Right, "Scene"),
        new(EditorWindows.Assets, $"{EditorIcons.Folder} Assets", DockArea.Bottom, "Project"),
        new(EditorWindows.Console, $"{EditorIcons.Terminal} Console", DockArea.Bottom, "Project"),
        new(EditorWindows.Scene, $"{EditorIcons.ViewInAr} Scene", DockArea.Center, "Scene"),
        new(EditorWindows.Stats, $"{EditorIcons.BarChart} Stats", DockArea.Bottom, "Project"),
    ];

    private static void Bind(EditorRegistrar registrar, string chord, string operatorId)
    {
        // A shipped binding that does not parse is a programming error, not a user's typo — the
        // keymap FILE tolerates one, this cannot.
        if (!Chord.TryParse(chord, out var parsed))
        {
            throw new InvalidOperationException($"'{chord}' is not a chord.");
        }
        registrar.AddKeyBinding(new KeyBinding(parsed, operatorId));
    }
}

/// <summary>Throw away the active workspace's saved arrangement and rebuild it.</summary>
public sealed class ResetLayoutOperator(EditorLayout layout) : IOperator
{
    public const string OperatorId = "editor.layout.reset";

    public string Id => OperatorId;
    public string Label => "Reset layout";
    public string Description => "Rebuild this workspace's panel arrangement from the default.";

    public bool IsAvailable(IOperatorContext context) => true;

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        layout.ResetActive();
        return OperatorResult.Finished;
    }
}

/// <summary>Open the command palette.</summary>
public sealed class OpenPaletteOperator(CommandPalette palette) : IOperator
{
    public const string OperatorId = "editor.palette.open";

    public string Id => OperatorId;
    public string Label => "Command palette";
    public string Description => "Search and run any registered command.";

    public bool IsAvailable(IOperatorContext context) => true;

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        palette.Open();
        return OperatorResult.Finished;
    }
}

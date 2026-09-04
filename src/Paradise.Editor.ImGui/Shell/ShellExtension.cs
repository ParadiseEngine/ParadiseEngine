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

    // Gaps, so panels and workspaces can grow without renumbering the fixed entries around them.
    private const int PanelMenuOrder = 20;
    private const int WorkspaceMenuOrder = 60;
    private const int ResetMenuOrder = 100;

    public void Register(EditorRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        registrar
            .AddOperator(new UndoOperator())
            .AddOperator(new RedoOperator())
            .AddOperator(new ResetLayoutOperator(shell.Layout))
            .AddOperator(new OpenPaletteOperator(shell.Palette));

        // A panel closes by its own X, and nothing else can reopen it — so every panel gets a
        // toggle operator, which is also what puts it in the command palette. Registered here
        // rather than by the panel so the rule holds for a panel an extension contributes too.
        var order = PanelMenuOrder;
        foreach (var window in Windows)
        {
            var panel = new PlaceholderPanel(window);
            registrar.AddWindow(window);
            shell.Windows.Add(registrar.Owner, panel);

            var toggle = new ToggleWindowOperator(panel);
            registrar.AddOperator(toggle);
            registrar.AddMenuEntry(new MenuEntry("View", window.Title, toggle.Id, order++));
        }
        var workspaceOrder = WorkspaceMenuOrder;
        foreach (var workspace in shell.Layout.Workspaces)
        {
            registrar.AddWorkspace(new WorkspaceDescriptor(workspace.Id, workspace.Title));
            var switchTo = new SwitchWorkspaceOperator(shell.Layout, workspace);
            registrar.AddOperator(switchTo);
            registrar.AddMenuEntry(new MenuEntry("View", workspace.Title, switchTo.Id, workspaceOrder++));
        }

        registrar
            .AddMenuEntry(new MenuEntry("Edit", "Undo", UndoOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("Edit", "Redo", RedoOperator.OperatorId, 1))
            .AddMenuEntry(new MenuEntry("View", "Command palette", OpenPaletteOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, PanelMenuOrder - 1))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, WorkspaceMenuOrder - 1))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, ResetMenuOrder - 1))
            .AddMenuEntry(new MenuEntry("View", "Reset layout", ResetLayoutOperator.OperatorId, ResetMenuOrder));

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

/// <summary>Show or hide one panel.</summary>
/// <remarks>The answer to "I closed it and cannot get it back". A panel's close box sets a flag
/// this operator is the only thing that clears, so without it the panel is gone until the editor
/// restarts — which is where this came from.</remarks>
public sealed class ToggleWindowOperator(EditorWindow window) : ICheckableOperator
{
    public string Id => $"{window.Descriptor.Id}.toggle";
    public string Label => window.Descriptor.Title;
    public string Description => $"Show or hide the {window.Descriptor.Title} panel.";

    public bool IsAvailable(IOperatorContext context) => true;

    public bool IsChecked(IOperatorContext context) => window.IsOpen;

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        window.IsOpen = !window.IsOpen;
        return OperatorResult.Finished;
    }
}

/// <summary>Make one workspace the active arrangement.</summary>
public sealed class SwitchWorkspaceOperator(EditorLayout layout, Workspace workspace) : ICheckableOperator
{
    public string Id => $"{workspace.Id}.activate";
    public string Label => workspace.Title;
    public string Description => $"Switch to the {workspace.Title} workspace.";

    public bool IsAvailable(IOperatorContext context) => layout.ActiveId != workspace.Id;

    public bool IsChecked(IOperatorContext context) => layout.ActiveId == workspace.Id;

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        layout.SwitchTo(workspace.Id);
        return OperatorResult.Finished;
    }
}

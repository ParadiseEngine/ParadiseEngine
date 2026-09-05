using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Input;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>The shell's own chrome: undo and redo, the palette, layout reset, and the workspaces.
/// </summary>
/// <remarks>
/// <para>
/// Registered through the same registrar an external extension uses, which is the claim in #222
/// that "built-in panels register through the same path an external extension would" kept honest
/// rather than asserted: if the shell needed a private route to add a menu item or a keybinding,
/// so would everyone else.
/// </para>
/// <para>
/// It contributes no PANELS. Each of those is its own extension under its own owner token (see
/// <c>Paradise.Editor.ImGui.Panels</c>), so a host can drop one without dropping the shell, and so
/// the claim above holds once per panel rather than once in total.
/// </para>
/// </remarks>
public sealed class ShellExtension : IShellExtension
{
    public const string OwnerId = "editor.shell";

    public string Id => OwnerId;

    // Gaps, so workspaces can grow without renumbering the fixed entries around them. Panel
    // ordering is the shell's, since panels arrive from every extension rather than only this one.
    private const int WorkspaceMenuOrder = 60;
    private const int ResetMenuOrder = 100;

    public void Register(ShellRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        var shell = registrar.Shell;

        registrar
            .AddOperator(new UndoOperator())
            .AddOperator(new RedoOperator())
            .AddOperator(new ResetLayoutOperator(shell.Layout))
            .AddOperator(new OpenPaletteOperator(shell.Palette));

        var workspaceOrder = WorkspaceMenuOrder;
        foreach (var workspace in shell.Layout.Workspaces)
        {
            registrar.Core.AddWorkspace(new WorkspaceDescriptor(workspace.Id, workspace.Title));
            var switchTo = new SwitchWorkspaceOperator(shell.Layout, workspace);
            registrar.AddOperator(switchTo);
            registrar.AddMenuEntry(new MenuEntry("View", workspace.Title, switchTo.Id, workspaceOrder++));
        }

        registrar
            .AddMenuEntry(new MenuEntry("Edit", "Undo", UndoOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("Edit", "Redo", RedoOperator.OperatorId, 1))
            .AddMenuEntry(new MenuEntry("View", "Command palette", OpenPaletteOperator.OperatorId, 0))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, 10))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, WorkspaceMenuOrder - 1))
            .AddMenuEntry(new MenuEntry("View", MenuEntry.Separator, string.Empty, ResetMenuOrder - 1))
            .AddMenuEntry(new MenuEntry("View", "Reset layout", ResetLayoutOperator.OperatorId, ResetMenuOrder));

        Bind(registrar, "Ctrl+Z", UndoOperator.OperatorId);
        Bind(registrar, "Ctrl+Shift+Z", RedoOperator.OperatorId);
        Bind(registrar, "Ctrl+Shift+P", OpenPaletteOperator.OperatorId);
    }

    private static void Bind(ShellRegistrar registrar, string chord, string operatorId)
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

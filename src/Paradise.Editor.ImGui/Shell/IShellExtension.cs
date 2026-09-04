using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Input;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>What an extension registers through: Core's registrar, plus the shell, plus the one
/// operation that needs both.</summary>
/// <remarks>
/// <para>
/// Core's <see cref="EditorRegistrar"/> cannot add a panel, because a panel is a drawing and Core
/// does not know drawings exist. This is the UI-layer half — it carries the same owner token, so
/// <c>RemoveOwner</c> still takes everything an extension added back out.
/// </para>
/// <para>
/// <see cref="AddPanel"/> exists because registering a panel is four steps that must all happen:
/// the descriptor, the drawing, a toggle operator, and the View entry that reaches it. Getting the
/// last two wrong produces a panel a user can close and never reopen — which is not hypothetical,
/// it is what shipped before someone tried it.
/// </para>
/// </remarks>
public sealed class ShellRegistrar(EditorShell shell, EditorRegistrar core)
{
    public EditorShell Shell => shell;

    /// <summary>Core's registrar, for anything with no UI in it.</summary>
    public EditorRegistrar Core => core;

    public OwnerToken Owner => core.Owner;

    /// <summary>Register a panel: its descriptor, its drawing, a toggle operator, and the View
    /// menu entry that reopens it.</summary>
    public ShellRegistrar AddPanel(EditorWindow panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var toggle = new ToggleWindowOperator(panel);
        core.AddWindow(panel.Descriptor);
        shell.Windows.Add(core.Owner, panel);
        core.AddOperator(toggle);
        core.AddMenuEntry(new MenuEntry("View", panel.Descriptor.Title, toggle.Id, shell.NextPanelMenuOrder()));
        return this;
    }

    public ShellRegistrar AddOperator(IOperator operatorInstance)
    {
        core.AddOperator(operatorInstance);
        return this;
    }

    public ShellRegistrar AddMenuEntry(MenuEntry entry)
    {
        core.AddMenuEntry(entry);
        return this;
    }

    public ShellRegistrar AddKeyBinding(KeyBinding binding)
    {
        core.AddKeyBinding(binding);
        return this;
    }
}

/// <summary>A unit of contribution to the editor's UI: panels, operators, menu items, keybindings.
/// </summary>
/// <remarks>
/// <para>
/// How a third party adds a view. The editor publishes ahead-of-time, so there is no loading a DLL
/// at runtime — NativeAOT has no JIT to compile one. An extension is therefore COMPILED IN: a
/// studio references <c>Paradise.Editor.Core</c> and <c>Paradise.Editor.ImGui</c>, implements this,
/// and builds their own editor executable, the same way a game already reaches the engine through
/// packages. The built-in shell registers through this exact interface, which is the claim in #222
/// being kept honest rather than asserted.
/// </para>
/// <para>
/// <c>Id</c> becomes the owner token, so unregistering it removes everything the extension added.
/// </para>
/// </remarks>
public interface IShellExtension
{
    string Id { get; }

    void Register(ShellRegistrar registrar);
}

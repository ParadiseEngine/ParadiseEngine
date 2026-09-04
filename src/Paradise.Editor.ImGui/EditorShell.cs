using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Input;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.ImGui;

/// <summary>The whole editor UI as one call per frame, inside a frame the host owns.</summary>
/// <remarks>
/// <para>
/// The order below is the only part of this class with an opinion. Keybindings resolve FIRST, so a
/// chord reaches its operator before a panel can eat the keystroke. The menu bar draws before the
/// dockspace because a main menu bar shrinks the viewport's work area, and a dockspace sized
/// against the wrong work area is a dockspace one menu-bar-height too tall. The palette draws
/// LAST, over everything, which is what a popup is for.
/// </para>
/// <para>
/// Panels arrive in E2. Until then this is the shell around an empty dockspace, which is exactly
/// what makes it worth having now: the layout, the menu, the palette and the keymap are the parts
/// every panel plugs into, and they are all testable without a single panel existing.
/// </para>
/// </remarks>
public sealed class EditorShell : IEditorShell
{
    // Panels sit between the palette and the workspaces in the View menu. A counter on the SHELL
    // rather than per-extension, so panels from two extensions interleave in registration order
    // instead of both starting at the same number.
    private const int PanelMenuOrderBase = 20;

    private readonly IOperatorDispatcher _dispatcher;
    private readonly IRegistry<KeyBinding> _keyBindings;
    private readonly Registry<EditorWindow> _windows = new();
    private int _panelMenuOrder = PanelMenuOrderBase;
    private readonly MainMenuBar _menuBar;
    private readonly CommandPalette _palette;

    public EditorShell(
        IOperatorDispatcher dispatcher,
        EditorRegistries registries,
        EditorLayout layout,
        CommandPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registries);

        _dispatcher = dispatcher;
        _keyBindings = registries.KeyBindings;
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _menuBar = new MainMenuBar(dispatcher, registries.Menus);
        _palette = palette ?? new CommandPalette(dispatcher, registries.Operators);
    }

    public EditorLayout Layout { get; }

    public CommandPalette Palette => _palette;

    /// <summary>Panels, by owner. Held here rather than in <c>EditorRegistries</c> because Core
    /// must not know a drawing exists; the descriptor there and the drawing here are joined by the
    /// window id.</summary>
    public IRegistry<EditorWindow> Windows => _windows;

    /// <summary>The next View-menu slot for a panel. Called by <c>ShellRegistrar.AddPanel</c>.</summary>
    public int NextPanelMenuOrder() => _panelMenuOrder++;

    /// <summary>Register <paramref name="extension"/> under its own owner token.</summary>
    public void Register(IShellExtension extension, EditorRegistries registries)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(registries);
        extension.Register(new ShellRegistrar(this, new EditorRegistrar(registries, new OwnerToken(extension.Id))));
    }

    /// <summary>The active input context, which decides which of two bindings on one chord wins.
    /// E2 sets it from the focused panel.</summary>
    public string? InputContext { get; set; }

    public void Draw()
    {
        DispatchChords();
        _menuBar.Draw();
        Layout.Draw();
        // After the dockspace, because a window submitted before the node exists is a window ImGui
        // has nowhere to put and floats instead.
        foreach (var window in _windows.Entries) window.Draw();
        _palette.Draw();
    }

    /// <summary>Run the operator bound to whatever was pressed this frame.</summary>
    /// <remarks>Built from the registry each frame rather than kept as a <see cref="Keymap"/>,
    /// because an extension may register a binding at any point and a keymap rebuilt on
    /// registration would need every registrar to know it exists. The list is a handful of entries;
    /// the cost is a walk, not a rebuild.</remarks>
    private void DispatchChords()
    {
        var bindings = _keyBindings.Entries;
        if (bindings.Count == 0) return;

        // Layered through Keymap rather than walked directly, so a context binding beats the
        // global one on the same chord instead of both firing — or worse, whichever happened to
        // be registered first winning.
        var keymap = Keymap.Empty.With(bindings, out _);
        foreach (var chord in bindings.Select(binding => binding.Chord).Distinct())
        {
            if (!ChordInput.WasPressed(chord)) continue;
            if (keymap.Resolve(chord, InputContext) is not { } operatorId) continue;

            _dispatcher.Dispatch(operatorId, OperatorArgs.None);
            return;
        }
    }
}

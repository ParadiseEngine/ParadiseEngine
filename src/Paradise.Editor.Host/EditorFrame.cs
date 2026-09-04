using Microsoft.Extensions.Logging;
using Paradise.Editor.Core;
using Paradise.Editor.Core.Document;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.ImGui;
using Paradise.Editor.ImGui.Shell;
using Zio;
using Zio.FileSystems;

namespace Paradise.Editor.Host;

/// <summary>The editor, composed for the standalone host: a session, the registries the shell
/// contributes to, and the shell itself.</summary>
/// <remarks>
/// <para>
/// The composition is HERE rather than in the editor library because it is the part that differs
/// between hosts: in-game the game supplies the scene provider, the filesystem and the layout
/// store, and constructs the same three objects around them. Nothing below this file knows which
/// host it is in.
/// </para>
/// <para>
/// One type for both run modes, so the headless capture and the windowed run cannot drift — a
/// screenshot that proves nothing about what a person sees is worse than no screenshot.
/// </para>
/// </remarks>
internal sealed class EditorFrame : IDisposable
{
    private readonly EditorShell _shell;
    private readonly IWorkspaceLayoutStore? _layouts;

    /// <param name="extensions">Contributed panels, operators, menu items and keybindings. The
    /// built-in shell registers first so its File/Edit/View menus stay leftmost; everything after
    /// it layers on, and each gets its own owner token so it can be removed as a unit.</param>
    public EditorFrame(
        IWorkspaceLayoutStore? layouts = null,
        ILogger? log = null,
        IEnumerable<IShellExtension>? extensions = null)
    {
        _layouts = layouts;

        // No project is open yet, so the document is empty and lives in memory. E3 swaps this for
        // a provider over assets/; nothing else here changes when it does.
        Session = new EditorSession(
            new InMemorySceneProvider(),
            new MemoryFileSystem(),
            HostCapabilities.Standalone,
            log);

        Registries = new EditorRegistries();
        Dispatcher = new OperatorDispatcher(Session, Registries.Operators, log);

        var layout = new EditorLayout(layouts);
        _shell = new EditorShell(Dispatcher, Registries, layout);

        _shell.Register(new ShellExtension(), Registries);
        foreach (var extension in extensions ?? []) _shell.Register(extension, Registries);
    }

    public EditorSession Session { get; }

    public EditorRegistries Registries { get; }

    public OperatorDispatcher Dispatcher { get; }

    public EditorLayout Layout => _shell.Layout;

    public void Draw() => _shell.Draw();

    public void Dispose() => _shell.Layout.Dispose();

    /// <summary>Persist the arrangement when ImGui says it changed. Called once a frame; ImGui
    /// raises the flag on its own timer rather than on every drag, so this is not a per-frame
    /// write.</summary>
    public void SaveLayoutIfChanged(bool wanted)
    {
        if (wanted && _layouts is not null) _shell.Layout.Save();
    }
}

using Paradise.Editor.ImGui.Panels;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.ImGui;

/// <summary>The extensions a stock editor is made of.</summary>
/// <remarks>
/// <para>
/// A LIST, not a hardcoded sequence inside the host, because the whole point of the split is that
/// a host can take a different set: filter one out before constructing, or drop it afterwards with
/// <see cref="EditorShell.Unregister(string, Paradise.Editor.Core.Extensibility.EditorRegistries)"/>.
/// A game embedding the editor to inspect its own world has no use for the Assets browser, and
/// nothing here should make that awkward.
/// </para>
/// <para>
/// Order is registration order, which is menu order: the shell first, so File and Edit stay
/// leftmost, then the panels in the order they read down the View menu.
/// </para>
/// </remarks>
public static class EditorExtensions
{
    public static IReadOnlyList<IShellExtension> BuiltIn { get; } =
    [
        new ShellExtension(),
        new HierarchyExtension(),
        new InspectorExtension(),
        new SceneExtension(),
        new AssetsExtension(),
        new ConsoleExtension(),
        new StatsExtension(),
    ];
}

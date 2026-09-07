using Zio;

namespace Paradise.Editor.Core.Persistence;

/// <summary>What lives in the <c>/user</c> mount, and where.</summary>
/// <remarks>
/// <para>
/// Per USER, not per project: the theme, the keymap override, the recent-projects list and one
/// dock layout per workspace. None of it belongs in a project, because none of it is something a
/// team shares — and putting a dock layout in <c>assets/</c> would put one developer's window
/// arrangement in everyone else's diff.
/// </para>
/// <para>
/// What the mount is over is the HOST's decision, the same as every other filesystem here: the
/// standalone host mounts the OS config directory, the in-game host may mount wherever the game
/// keeps its settings, and a test mounts memory. Core never learns an OS path.
/// </para>
/// </remarks>
public static class EditorUserLayout
{
    public static UPath Settings => "/settings.toml";

    /// <summary>The user's overrides, layered over the shipped preset.</summary>
    public static UPath Keymap => "/keymap.toml";

    /// <summary>One ImGui ini per workspace, so switching workspaces does not overwrite the layout
    /// of the one being left.</summary>
    public static UPath LayoutFor(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return UPath.Root / "layouts" / $"{workspaceId}.ini";
    }
}

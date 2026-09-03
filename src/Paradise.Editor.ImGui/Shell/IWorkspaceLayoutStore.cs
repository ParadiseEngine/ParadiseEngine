namespace Paradise.Editor.ImGui.Shell;

/// <summary>Where a workspace's window arrangement is kept between runs.</summary>
/// <remarks>
/// <para>
/// An interface rather than a direct call into <c>ImGuiUiCore</c> because the two hosts reach the
/// layout differently and neither of them is the shell's business: standalone it is the
/// <c>/user</c> mount, in-game it is wherever the game keeps its settings, and a test wants
/// neither. It also keeps the object that RUNS the frame out of the layer that must not.
/// </para>
/// <para>
/// One file per workspace. Sharing one would mean switching workspaces overwrites the arrangement
/// of the one being left, which is the opposite of what a workspace is for.
/// </para>
/// </remarks>
public interface IWorkspaceLayoutStore
{
    /// <summary>Load <paramref name="workspaceId"/>'s arrangement into ImGui. False when there is
    /// none — a fresh profile, or one just reset — which is the shell's cue to seed the default.</summary>
    bool TryLoad(string workspaceId);

    void Save(string workspaceId);

    /// <summary>Forget the saved arrangement. What "Reset layout" does before re-seeding.</summary>
    void Delete(string workspaceId);
}

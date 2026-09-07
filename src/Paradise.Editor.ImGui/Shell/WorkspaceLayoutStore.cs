using Paradise.Editor.Core.Persistence;
using Paradise.Ui.ImGui;
using Zio;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>The standalone implementation: ImGui's own ini text, one file per workspace, in the
/// mount the host chose for <c>/user</c>.</summary>
/// <remarks>Layout crosses as a STRING rather than through ImGui's file IO, because ImGui's is not
/// redirectable — <c>ImGuiUiCore.SaveLayout</c> carries the reasoning. The consequence here is the
/// useful part: the arrangement lands wherever the host mounted, so a portable install and a
/// per-user config directory are the same code.</remarks>
public sealed class WorkspaceLayoutStore(ImGuiUiCore core, IFileSystem userMount) : IWorkspaceLayoutStore
{
    public bool TryLoad(string workspaceId) =>
        core.TryLoadLayout(userMount, EditorUserLayout.LayoutFor(workspaceId));

    public void Save(string workspaceId) =>
        core.SaveLayout(userMount, EditorUserLayout.LayoutFor(workspaceId));

    public void Delete(string workspaceId)
    {
        var path = EditorUserLayout.LayoutFor(workspaceId);
        if (userMount.FileExists(path)) userMount.DeleteFile(path);
    }
}

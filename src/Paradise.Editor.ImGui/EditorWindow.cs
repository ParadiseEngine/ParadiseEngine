using ImGuiNET;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.ImGui;

/// <summary>One dockable panel: the descriptor Core knows it by, plus how it draws.</summary>
/// <remarks>Holds no editor state. A panel reads Core each frame and dispatches operators to
/// change anything; the only fields a subclass may keep are presentation details meaningless
/// outside it, such as a filter string or a scroll flag.</remarks>
public abstract class EditorWindow(WindowDescriptor descriptor)
{
    public WindowDescriptor Descriptor => descriptor;

    public bool IsOpen { get; set; } = true;

    protected virtual ImGuiWindowFlags Flags => ImGuiWindowFlags.None;

    /// <summary>Draw this window into the current frame; the host has already begun it.</summary>
    public void Draw()
    {
        if (!IsOpen) return;
        var open = IsOpen;
        if (global::ImGuiNET.ImGui.Begin(descriptor.Title, ref open, Flags))
        {
            DrawContent();
        }
        global::ImGuiNET.ImGui.End();
        IsOpen = open;
    }

    protected abstract void DrawContent();
}

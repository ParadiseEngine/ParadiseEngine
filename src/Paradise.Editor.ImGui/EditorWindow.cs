using Hexa.NET.ImGui;
using Paradise.Editor.Core.Shell;
// The enclosing namespace ends in .ImGui, which hides the type of the same name;
// Paradise.Ui.ImGui aliases it for exactly this reason.
using ImGuiApi = Hexa.NET.ImGui.ImGui;

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

    // "Title###id": ImGui keys the window on what follows ###, so layout persistence and docking
    // survive a renamed or localised title, and two panels may share one.
    private readonly string _label = $"{descriptor.Title}###{descriptor.Id}";

    /// <summary>Draw this window into the current frame; the host has already begun it.</summary>
    /// <remarks><c>End</c> is unconditional and in a <c>finally</c> for the same reason
    /// <c>OperatorDispatcher</c> contains a throwing operator: an exception leaving a panel
    /// mid-frame unbalances Begin/End, and the frame that then breaks is the NEXT one, somewhere
    /// that names neither this window nor the panel that threw. Containing operators is not
    /// enough — a field renderer or any drawing code throws straight through here, and this is
    /// the one place every panel shares.</remarks>
    public void Draw()
    {
        if (!IsOpen) return;
        var open = IsOpen;
        try
        {
            if (ImGuiApi.Begin(_label, ref open, Flags))
            {
                DrawContent();
            }
        }
        finally
        {
            ImGuiApi.End();
        }
        IsOpen = open;
    }

    protected abstract void DrawContent();
}

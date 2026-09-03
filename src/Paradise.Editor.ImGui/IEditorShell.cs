namespace Paradise.Editor.ImGui;

/// <summary>The whole editor UI as one call per frame, inside a frame the host owns.</summary>
/// <remarks>Dockspace, menu bar, command palette and every registered window are drawn here;
/// nothing else in this assembly is an entry point. Hosts differ only in who runs the frame and
/// forwards input, which is exactly the contract Paradise.Ui's <c>IUiInput</c> already covers.</remarks>
public interface IEditorShell
{
    void Draw();
}

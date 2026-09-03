using Paradise.Editor.Core.Shell;
using Paradise.Ui.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>A panel with a title, a dock position and nothing in it yet.</summary>
/// <remarks>
/// <para>
/// These exist so E1 is checkable. A dock recipe that positions six windows is only as good as the
/// windows being there: ImGui builds the nodes either way, so a recipe with a typo in a window id
/// produces the same node graph and an empty screen, and nobody finds out until E2. With a
/// placeholder per registered descriptor, the arrangement is visible in the smoke capture and the
/// ids are proven to line up.
/// </para>
/// <para>
/// E2 replaces the body of each. The <see cref="EditorWindow"/> around it — the id-keyed label, the
/// close box, the Begin/End balance — is what stays.
/// </para>
/// </remarks>
public sealed class PlaceholderPanel(WindowDescriptor descriptor) : EditorWindow(descriptor)
{
    protected override void DrawContent()
    {
        ImGuiText.Disabled($"{Descriptor.Title} arrives in E2.");
        ImGuiApi.Separator();
        ImGuiText.Disabled(Descriptor.Id);
    }
}

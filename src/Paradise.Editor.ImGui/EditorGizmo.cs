using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>Sets up ImGuizmo and exposes transform manipulation.</summary>
/// <remarks>
/// ImGuizmo's native library has a separate ImGui context; call <see cref="Attach"/> to avoid a
/// native null dereference. <see cref="Manipulate"/> fixes view/projection argument ordering;
/// other ImGuizmo APIs are used directly.
/// </remarks>
public static class EditorGizmo
{
    /// <summary>Give ImGuizmo's native the ImGui context this process is using. Call once, after
    /// the context exists and before any other member here.</summary>
    public static void Attach() => ImGuizmo.SetImGuiContext(ImGuiApi.GetCurrentContext());

    /// <summary>Open a gizmo pass covering one window's content area.</summary>
    /// <remarks>Call from INSIDE the panel's <c>Begin</c>/<c>End</c>. The draw list is set
    /// explicitly to the current window's: ImGuizmo otherwise draws into the foreground list,
    /// which ignores the panel's clip rect — so a gizmo belonging to a scrolled or partially
    /// covered viewport paints over whatever is on top of it.</remarks>
    public static void BeginFrame(Vector2 position, Vector2 size)
    {
        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist(ImGuiApi.GetWindowDrawList());
        ImGuizmo.SetRect(position.X, position.Y, size.X, size.Y);
    }

    /// <summary>Draw the handles for <paramref name="model"/> and apply a drag to it. True while
    /// the user is holding one.</summary>
    /// <remarks>Matrices are passed by value because ImGuizmo takes all three by reference and
    /// writes only the last; taking view and projection as <c>in</c> here would mean copying them
    /// anyway, and taking them as <c>ref</c> would suggest it writes them.</remarks>
    public static bool Manipulate(
        Matrix4x4 view,
        Matrix4x4 projection,
        ImGuizmoOperation operation,
        ImGuizmoMode mode,
        ref Matrix4x4 model) =>
        ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref model);

    /// <summary>True while a drag is in progress on any gizmo — what a Scene panel checks before
    /// treating a click as a selection.</summary>
    public static bool IsUsing => ImGuizmo.IsUsingAny();
}

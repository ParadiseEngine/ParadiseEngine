using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>Transform handles for the Scene panel, and the one piece of setup ImGuizmo cannot do
/// for itself.</summary>
/// <remarks>
/// <para>
/// <b>ImGuizmo ships its own native.</b> <c>cimguizmo</c> statically links its own copy of Dear
/// ImGui, so it has a <c>GImGui</c> of its own and cannot see the context <c>cimgui</c> created.
/// <see cref="Attach"/> hands ours across. Forgetting it does not throw — it dereferences null
/// inside native code, which surfaces as a process death with no managed stack, the same shape as
/// the <c>GetID</c> failure recorded in <c>.claude/lessons.md</c>.
/// </para>
/// <para>
/// The wrappers here exist for one reason each, not to hide the library: <see cref="Attach"/>
/// because the contract above has to live somewhere it will be found, and
/// <see cref="Manipulate"/> because ImGuizmo takes view and projection BEFORE the matrix it
/// mutates, and a caller that swaps them gets a gizmo that renders and simply never grabs.
/// Everything else — <c>DrawGrid</c>, <c>ViewManipulate</c>, <c>DecomposeMatrixToComponents</c> —
/// is called directly off <see cref="ImGuizmo"/>.
/// </para>
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

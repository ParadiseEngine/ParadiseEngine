using System.Numerics;
using Hexa.NET.ImGuizmo;
using Paradise.Editor.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Test;

/// <summary>ImGuizmo through OUR context.</summary>
/// <remarks>Worth a test of its own rather than waiting for the Scene panel, because the thing
/// that can go wrong is not the gizmo maths — it is that <c>cimguizmo</c> statically links its own
/// Dear ImGui and therefore its own <c>GImGui</c>. Without <c>SetImGuiContext</c> it does not draw
/// into our frame; it dereferences null in native code. Proving the handoff here means E3 inherits
/// a contract that is already known to hold.</remarks>
[NotInParallel]
public class GizmoTests
{
    private static readonly Matrix4x4 View =
        Matrix4x4.CreateLookAt(new Vector3(0f, 2f, 6f), Vector3.Zero, Vector3.UnitY);

    private static readonly Matrix4x4 Projection =
        Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.6f, 0.1f, 100f);

    private static int DrawPanel(EditorImGuiContext context, bool withGizmo, ref Matrix4x4 model)
    {
        var local = model;
        var data = context.Frame(() =>
        {
            ImGuiApi.Begin("Scene");
            if (withGizmo)
            {
                EditorGizmo.BeginFrame(ImGuiApi.GetWindowPos(), ImGuiApi.GetWindowSize());
                EditorGizmo.Manipulate(View, Projection, ImGuizmoOperation.Translate, ImGuizmoMode.World, ref local);
            }
            ImGuiApi.End();
        });
        model = local;
        return data.TotalVtxCount;
    }

    [Test]
    public async Task a_gizmo_contributes_geometry_to_the_frame_the_editor_already_draws()
    {
        using var context = new EditorImGuiContext();
        EditorGizmo.Attach();
        var model = Matrix4x4.Identity;

        // Settled first: an ImGui window's size and scroll are not final on the frame it appears,
        // so comparing against frame one would measure the window arriving, not the gizmo.
        var withoutGizmo = 0;
        for (var frame = 0; frame < 3; frame++) withoutGizmo = DrawPanel(context, withGizmo: false, ref model);
        var withGizmo = DrawPanel(context, withGizmo: true, ref model);

        await Assert.That(withoutGizmo).IsGreaterThan(0);
        await Assert.That(withGizmo).IsGreaterThan(withoutGizmo);
    }

    // Nothing is being dragged, so the matrix must come back exactly as it went in. This is the
    // assertion that catches the argument order being wrong: view, projection, THEN the matrix
    // ImGuizmo writes — swap them and the "model" it mutates is the caller's view matrix.
    [Test]
    public async Task an_untouched_gizmo_leaves_its_matrix_alone()
    {
        using var context = new EditorImGuiContext();
        EditorGizmo.Attach();
        var model = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var original = model;

        for (var frame = 0; frame < 3; frame++) DrawPanel(context, withGizmo: true, ref model);

        await Assert.That(model).IsEqualTo(original);
        await Assert.That(EditorGizmo.IsUsing).IsFalse();
    }
}

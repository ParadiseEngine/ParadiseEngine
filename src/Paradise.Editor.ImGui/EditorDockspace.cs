using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>The dockspace every editor panel docks into, and the seeding of its default layout.
/// </summary>
/// <remarks>
/// <para>
/// <c>PassthruCentralNode</c> because the central node is where the Scene viewport goes: the
/// dockspace host window must not paint over the frame the renderer already drew there.
/// </para>
/// <para>
/// The layout is seeded only when there is NO node — a fresh profile, or after a reset. An ini
/// restored through <c>ImGuiUiCore.TryLoadLayout</c> brings its own node graph, and rebuilding
/// over it would silently discard the arrangement the user made. That is also why the rebuild
/// tests the node rather than a "first frame" flag: whether a layout was restored is a fact about
/// ImGui's state, not about how many frames have passed.
/// </para>
/// <para>
/// <paramref name="seedLayout"/> runs INSIDE the builder transaction, between the root node being
/// sized and <c>DockBuilderFinish</c>. Splitting or docking outside that window silently does
/// nothing, which is the failure this seam exists to make unrepeatable. E1 passes the editor's
/// real recipe; E0 docks one window so the dockspace is demonstrably load-bearing.
/// </para>
/// </remarks>
public sealed class EditorDockspace(string id = "EditorDockspace", Action<uint>? seedLayout = null)
{
    private bool _rebuild;

    /// <summary>The root node's id.</summary>
    /// <remarks>
    /// Hashed directly rather than through <c>ImGui.GetID</c>, which seeds from the CURRENT
    /// WINDOW's id stack. Two consequences made that the wrong primitive here, and the second one
    /// segfaults: the id would change if this were ever drawn inside a Begin/End — silently losing
    /// the saved layout — and it cannot be computed between frames at all, because there is no
    /// current window to seed from, so a host or a test asking whether the node exists after the
    /// frame dereferences null. <c>ImHashStr</c> is a pure function of the string.
    /// </remarks>
    public uint NodeId { get; } = ImGuiP.ImHashStr(id);

    /// <summary>Drop the current layout and re-seed on the next <see cref="Draw"/>.</summary>
    /// <remarks>Deferred rather than immediate because the builder may not run while the frame it
    /// would rearrange is in progress — the "Reset layout" menu item is itself drawn mid-frame.</remarks>
    public void ResetLayout() => _rebuild = true;

    /// <summary>Establish the dockspace for this frame. Call once, before any dockable window.</summary>
    public void Draw()
    {
        var viewport = ImGuiApi.GetMainViewport();

        if (_rebuild || ImGuiP.DockBuilderGetNode(NodeId).IsNull)
        {
            _rebuild = false;
            ImGuiP.DockBuilderRemoveNode(NodeId);
            // The private DockSpace flag: without it the node is a floating root rather than one
            // DockSpaceOverViewport will adopt, and the seeded layout is discarded on the frame it
            // was built.
            ImGuiP.DockBuilderAddNode(NodeId, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
            ImGuiP.DockBuilderSetNodeSize(NodeId, viewport.WorkSize);
            seedLayout?.Invoke(NodeId);
            ImGuiP.DockBuilderFinish(NodeId);
        }

        ImGuiApi.DockSpaceOverViewport(NodeId, viewport, ImGuiDockNodeFlags.PassthruCentralNode);
    }

    /// <summary>Dock a window by its ImGui title into <paramref name="node"/>. Only meaningful
    /// from inside a <c>seedLayout</c> callback.</summary>
    public static void Dock(string windowTitle, uint node) => ImGuiP.DockBuilderDockWindow(windowTitle, node);

    /// <summary>Split <paramref name="node"/>, returning the new side and leaving the remainder as
    /// <paramref name="node"/> — the shape a recipe of successive splits wants.</summary>
    public static uint Split(ref uint node, ImGuiDir direction, float ratio)
    {
        uint side = 0;
        uint remainder = 0;
        unsafe
        {
            ImGuiP.DockBuilderSplitNode(node, direction, ratio, &side, &remainder);
        }
        node = remainder;
        return side;
    }

    /// <summary>Docking is a config flag, and ImGui silently ignores every dockspace call without
    /// it. The in-game host owns its own core, so this is what an editor asks of either host.</summary>
    public static void EnableDocking()
    {
        var io = ImGuiApi.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
    }

    /// <summary>Whether the layout ImGui currently holds has a node for this dockspace. Safe
    /// between frames, which is the point — it is how a host and a test check the dockspace was
    /// actually built.</summary>
    public bool HasNode => !ImGuiP.DockBuilderGetNode(NodeId).IsNull;

    /// <summary>Size of the root node, or zero when there is none.</summary>
    public Vector2 NodeSize
    {
        get
        {
            var node = ImGuiP.DockBuilderGetNode(NodeId);
            return node.IsNull ? Vector2.Zero : node.Size;
        }
    }
}

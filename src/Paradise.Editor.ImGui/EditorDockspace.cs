using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>Draws the dockspace and seeds its default layout when no node exists.</summary>
/// <remarks>
/// <c>PassthruCentralNode</c> leaves the rendered scene visible beneath the host window.
/// Retain restored node graphs; rebuilding them would discard the user's layout.
/// Run <paramref name="seedLayout"/> after sizing the root and before <c>DockBuilderFinish</c>.
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

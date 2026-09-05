using Hexa.NET.ImGui;
using Paradise.Editor.Core.Shell;
using Paradise.Editor.ImGui.Shell;

namespace Paradise.Editor.ImGui;

/// <summary>A named arrangement of panels: its id, its title, and the recipe that builds it from
/// nothing.</summary>
/// <remarks>The recipe runs ONLY when there is no saved arrangement — a fresh profile, or one the
/// user just reset. It receives the root node and splits it; see <see cref="EditorLayout"/>.</remarks>
public sealed record Workspace(string Id, string Title, Action<uint> Seed);

/// <summary>The dockspace, the workspaces that arrange it, and where their arrangements are kept.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="EditorDockspace"/> per workspace, each with its own node id and its own saved
/// ini, because a workspace IS an arrangement — sharing either would make switching overwrite the
/// one being left.
/// </para>
/// <para>
/// A panel added by an upgrade lands in its default area rather than discarding the user's
/// arrangement: the saved ini is loaded first, and the recipe only runs when there is none. ImGui
/// places a window it has no saved position for using whatever the recipe or the panel asks, so
/// the new one appears and the old ones stay where they were put.
/// </para>
/// </remarks>
public sealed class EditorLayout : IDisposable
{
    private readonly IWorkspaceLayoutStore? _store;
    private readonly List<Workspace> _workspaces = [];
    private readonly Dictionary<string, EditorDockspace> _dockspaces = [];
    private string? _pendingSwitch;

    public EditorLayout(IWorkspaceLayoutStore? store = null)
    {
        _store = store;
        Add(Default);
        ActiveId = Default.Id;
    }

    /// <summary>The shipped arrangement: Hierarchy left, Inspector right, Assets and Console
    /// sharing the bottom, Scene in what remains.</summary>
    /// <remarks>Split off the ROOT in that order, and the order matters: each split takes its
    /// fraction of what is left, so doing the bottom first would make the side columns short
    /// rather than full height.</remarks>
    public static Workspace Default { get; } = new(
        EditorWorkspaces.Level,
        "Level",
        root =>
        {
            var left = EditorDockspace.Split(ref root, ImGuiDir.Left, 0.20f);
            var right = EditorDockspace.Split(ref root, ImGuiDir.Right, 0.25f);
            var bottom = EditorDockspace.Split(ref root, ImGuiDir.Down, 0.30f);

            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Hierarchy), left);
            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Inspector), right);
            // Two panels into one node is a tab bar, which is what "Assets | Console" means.
            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Assets), bottom);
            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Console), bottom);
            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Stats), bottom);
            EditorDockspace.Dock(EditorDockspace.LabelFor(EditorWindows.Scene), root);
        });

    public IReadOnlyList<Workspace> Workspaces => _workspaces;

    public string ActiveId { get; private set; }

    public Workspace Active => _workspaces.First(workspace => workspace.Id == ActiveId);

    /// <summary>The dockspace node the active workspace is arranged in, once <see cref="Draw"/>
    /// has run at least once.</summary>
    public uint ActiveNode => _dockspaces.TryGetValue(ActiveId, out var dockspace) ? dockspace.NodeId : 0;

    public void Add(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (_workspaces.Any(existing => existing.Id == workspace.Id))
        {
            throw new InvalidOperationException($"A workspace with id '{workspace.Id}' is already registered.");
        }
        _workspaces.Add(workspace);
    }

    /// <summary>Switch on the NEXT frame.</summary>
    /// <remarks>Deferred because this is called from a menu item, which is drawn inside the frame
    /// whose dockspace it would be tearing down. Swapping mid-frame leaves every window already
    /// submitted parented to a node that no longer exists.</remarks>
    public void SwitchTo(string workspaceId)
    {
        if (_workspaces.All(workspace => workspace.Id != workspaceId))
        {
            throw new InvalidOperationException($"No workspace with id '{workspaceId}' is registered.");
        }
        _pendingSwitch = workspaceId;
    }

    /// <summary>Throw away the active workspace's saved arrangement and rebuild it from the
    /// recipe, on the next frame.</summary>
    public void ResetActive()
    {
        _store?.Delete(ActiveId);
        Dockspace(ActiveId).ResetLayout();
    }

    /// <summary>Persist the active arrangement. The host calls this when ImGui says the layout
    /// changed, and on shutdown.</summary>
    public void Save() => _store?.Save(ActiveId);

    /// <summary>Establish the active workspace's dockspace for this frame. Call once, before any
    /// dockable window.</summary>
    public void Draw()
    {
        if (_pendingSwitch is { } next)
        {
            _pendingSwitch = null;
            if (next != ActiveId)
            {
                Save();
                ActiveId = next;
                _store?.TryLoad(next);
            }
        }

        Dockspace(ActiveId).Draw();
    }

    /// <summary>Release every workspace's dockspace name.</summary>
    public void Dispose()
    {
        foreach (var dockspace in _dockspaces.Values) dockspace.Dispose();
        _dockspaces.Clear();
    }

    private EditorDockspace Dockspace(string workspaceId)
    {
        if (_dockspaces.TryGetValue(workspaceId, out var existing)) return existing;

        var workspace = _workspaces.First(candidate => candidate.Id == workspaceId);
        // Loaded BEFORE the dockspace is constructed, so that by the time Draw asks whether a node
        // exists, a restored ini has already created one and the recipe stays unrun.
        _store?.TryLoad(workspaceId);
        var dockspace = new EditorDockspace($"dockspace::{workspaceId}", workspace.Seed);
        _dockspaces[workspaceId] = dockspace;
        return dockspace;
    }
}

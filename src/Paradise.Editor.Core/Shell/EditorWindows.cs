namespace Paradise.Editor.Core.Shell;

/// <summary>The ids of the editor's own panels, and of its workspaces.</summary>
/// <remarks>Constants rather than strings at each site because they are the JOIN between three
/// things that are written in different places and at different times: a panel registers a
/// <see cref="WindowDescriptor"/> under one, a dock recipe positions it by the same one, and a
/// saved layout on a user's disk refers to it a year later. A typo in any of those is a panel that
/// silently opens somewhere else.</remarks>
public static class EditorWindows
{
    public const string Hierarchy = "editor.window.hierarchy";
    public const string Inspector = "editor.window.inspector";
    public const string Assets = "editor.window.assets";
    public const string Console = "editor.window.console";
    public const string Scene = "editor.window.scene";
    public const string Stats = "editor.window.stats";
}

/// <summary>The ids of the shipped workspaces.</summary>
public static class EditorWorkspaces
{
    /// <summary>The one a fresh profile opens in.</summary>
    public const string Level = "editor.workspace.level";
}

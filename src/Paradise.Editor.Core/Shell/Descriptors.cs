using Paradise.Editor.Core.Input;

namespace Paradise.Editor.Core.Shell;

public enum DockArea
{
    Left,
    Center,
    Right,
    Bottom,
}

/// <summary>A window the shell can show: identity and where it goes by default. How it is
/// drawn belongs to the UI layer, which maps the id to a drawing.</summary>
public sealed record WindowDescriptor(string Id, string Title, DockArea DefaultArea, string Category);

/// <summary>A named layout of windows. The dock recipe itself is UI-layer data keyed by this id.</summary>
public sealed record WorkspaceDescriptor(string Id, string Title);

/// <summary>A menu item is a label pointing at an operator; it holds no code.</summary>
public sealed record MenuEntry(string Menu, string Label, string OperatorId, int Order = 0);

/// <summary>A chord pointing at an operator, optionally only while an input context is active.</summary>
/// <remarks>The chord is PARSED, not a string: it is compared against every key event, and a
/// binding whose spelling was never checked would fail silently at the moment somebody presses
/// it rather than at the moment it was registered.</remarks>
public sealed record KeyBinding(Chord Chord, string OperatorId, string? Context = null);

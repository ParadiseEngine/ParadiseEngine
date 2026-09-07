using Hexa.NET.ImGui;
using Paradise.Editor.Core.Input;
using Paradise.Windowing;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>Reads chords out of ImGui's key state.</summary>
/// <remarks>
/// <para>
/// Through ImGui rather than off the host's <c>WindowEvent</c> stream, because that is the one
/// path both hosts share: standalone the events reach ImGui through <c>ImGuiUiCore</c>, in-game
/// through the game's <c>CompositeUiInput</c>, and the editor sees neither directly. It also means
/// a chord cannot fire while a text field has focus, since ImGui knows that and a raw event stream
/// does not.
/// </para>
/// <para>
/// The mapping is by NAME wherever the two enums agree, which is most of them, with a table for
/// the places they do not. A name-based fallback alone would silently fail on the digits, and a
/// chord that never fires is the hardest kind of binding to debug.
/// </para>
/// </remarks>
public static class ChordInput
{
    /// <summary>The ImGui key for <paramref name="key"/>, or <see cref="ImGuiKey.None"/>.</summary>
    public static ImGuiKey ToImGuiKey(KeyboardKey key) => key switch
    {
        KeyboardKey.None => ImGuiKey.None,
        // ImGui spells the digit row _0.._9; every other name below matches.
        >= KeyboardKey.Digit0 and <= KeyboardKey.Digit9 =>
            ImGuiKey.Key0 + (key - KeyboardKey.Digit0),
        KeyboardKey.Up => ImGuiKey.UpArrow,
        KeyboardKey.Down => ImGuiKey.DownArrow,
        KeyboardKey.Left => ImGuiKey.LeftArrow,
        KeyboardKey.Right => ImGuiKey.RightArrow,
        KeyboardKey.LeftControl => ImGuiKey.LeftCtrl,
        KeyboardKey.RightControl => ImGuiKey.RightCtrl,
        _ => Enum.TryParse<ImGuiKey>(key.ToString(), out var parsed) ? parsed : ImGuiKey.None,
    };

    /// <summary>Whether <paramref name="chord"/> was pressed this frame, with exactly its
    /// modifiers.</summary>
    /// <remarks>EXACTLY: a Ctrl+S binding must not fire on Ctrl+Shift+S, or the more specific
    /// chord can never be bound to anything else.</remarks>
    public static bool WasPressed(Chord chord)
    {
        var key = ToImGuiKey(chord.Key);
        if (key == ImGuiKey.None || !ImGuiApi.IsKeyPressed(key, false)) return false;

        var io = ImGuiApi.GetIO();
        return io.KeyCtrl == chord.Modifiers.HasFlag(ChordModifiers.Control)
            && io.KeyShift == chord.Modifiers.HasFlag(ChordModifiers.Shift)
            && io.KeyAlt == chord.Modifiers.HasFlag(ChordModifiers.Alt);
    }
}

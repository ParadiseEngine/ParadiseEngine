using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>Text helpers that render a string as a string.
///
/// <b>Why these exist.</b> <c>ImGui.Text</c>, <c>TextColored</c>, <c>TextWrapped</c> and
/// <c>TextDisabled</c> take a printf FORMAT, and the binding passes it straight to cimgui's
/// varargs entry point. Any runtime string containing a percent sign is then interpreted:
/// "50% done" prints garbage, and an unmatched <c>%s</c> reads a pointer that was never pushed
/// and segfaults the process (HexaEngine/Hexa.NET.ImGui#130 — ImGui.NET had the identical
/// hazard). Only <c>TextUnformatted</c> is safe, and it has no colored/wrapped/disabled
/// variants — so these rebuild them from the style stack, which is what Dear ImGui's own
/// implementations do underneath the formatting.
///
/// Call these instead of <c>ImGui.Text*</c> for anything that is not a compile-time literal
/// under your own control.</summary>
public static class ImGuiText
{
    /// <summary>Draw <paramref name="text"/> verbatim.</summary>
    public static void Show(string text) => ImGuiApi.TextUnformatted(text);

    /// <summary>Draw <paramref name="text"/> verbatim in <paramref name="color"/> (RGBA, 0..1).</summary>
    public static void Colored(Vector4 color, string text)
    {
        ImGuiApi.PushStyleColor(ImGuiCol.Text, color);
        ImGuiApi.TextUnformatted(text);
        ImGuiApi.PopStyleColor();
    }

    /// <summary>Draw <paramref name="text"/> verbatim in the style's disabled color.</summary>
    public static void Disabled(string text)
    {
        Colored(ImGuiApi.GetStyle().Colors[(int)ImGuiCol.TextDisabled], text);
    }

    /// <summary>Draw <paramref name="text"/> verbatim, wrapped at the window's right edge.</summary>
    public static void Wrapped(string text)
    {
        // 0 means "wrap at the end of the window's work rect" — the same default TextWrapped uses.
        ImGuiApi.PushTextWrapPos(0f);
        ImGuiApi.TextUnformatted(text);
        ImGuiApi.PopTextWrapPos();
    }
}

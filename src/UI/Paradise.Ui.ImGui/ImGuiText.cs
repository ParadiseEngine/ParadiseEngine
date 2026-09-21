using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>Renders runtime strings without printf interpretation.</summary>
/// <remarks>ImGui.Text variants pass strings to native varargs: percent sequences can print garbage
/// or dereference missing arguments. These helpers use TextUnformatted and the style stack; use
/// them for runtime text.</remarks>
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

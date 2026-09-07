using System.Numerics;
using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>The editor's dark theme: every colour in one place, derived from a handful of tokens.
/// </summary>
/// <remarks>
/// <para>
/// Tokens rather than 60 literals, because ImGui's palette is 60 slots and hand-tuning each is how
/// a theme ends up with four slightly different greys nobody chose. Everything here is one of the
/// surfaces or the accent, lightened or darkened.
/// </para>
/// <para>
/// One theme, and no runtime switching yet. A second theme is a token set, not a code path, which
/// is the reason to spend the tokens now rather than a theme system later.
/// </para>
/// </remarks>
public static class EditorTheme
{
    // Neutrals carry a slight blue bias rather than being pure grey, so the accent sits in the
    // same family as the surfaces instead of on top of them.
    private static readonly Vector4 Background = Rgb(0x14, 0x17, 0x1C);
    private static readonly Vector4 Surface = Rgb(0x1B, 0x1F, 0x26);
    private static readonly Vector4 SurfaceRaised = Rgb(0x24, 0x29, 0x33);
    private static readonly Vector4 Border = Rgb(0x2E, 0x34, 0x40);
    private static readonly Vector4 Text = Rgb(0xDC, 0xE1, 0xE8);
    private static readonly Vector4 TextMuted = Rgb(0x8A, 0x93, 0xA3);
    private static readonly Vector4 Accent = Rgb(0x4C, 0x8E, 0xDA);

    /// <summary>Apply to the current context. Call once, after the context exists.</summary>
    public static void Apply()
    {
        var style = ImGuiApi.GetStyle();

        style.WindowRounding = 4f;
        style.ChildRounding = 4f;
        style.FrameRounding = 4f;
        style.PopupRounding = 4f;
        style.ScrollbarRounding = 6f;
        style.GrabRounding = 4f;
        style.TabRounding = 4f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new Vector2(8f, 8f);
        style.FramePadding = new Vector2(8f, 4f);
        style.ItemSpacing = new Vector2(8f, 5f);
        style.ItemInnerSpacing = new Vector2(6f, 4f);
        style.IndentSpacing = 18f;
        style.ScrollbarSize = 12f;
        style.GrabMinSize = 10f;
        // Panels are read left to right; a centred title in a dock tab reads as decoration.
        style.WindowTitleAlign = new Vector2(0f, 0.5f);

        Set(ImGuiCol.WindowBg, Background);
        Set(ImGuiCol.ChildBg, Background);
        Set(ImGuiCol.PopupBg, Surface);
        Set(ImGuiCol.Border, Border);
        Set(ImGuiCol.BorderShadow, Transparent);

        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, TextMuted);

        Set(ImGuiCol.FrameBg, Surface);
        Set(ImGuiCol.FrameBgHovered, SurfaceRaised);
        Set(ImGuiCol.FrameBgActive, Lighten(SurfaceRaised, 0.08f));

        Set(ImGuiCol.TitleBg, Surface);
        Set(ImGuiCol.TitleBgActive, SurfaceRaised);
        Set(ImGuiCol.TitleBgCollapsed, Surface);
        Set(ImGuiCol.MenuBarBg, Surface);

        Set(ImGuiCol.ScrollbarBg, Background);
        Set(ImGuiCol.ScrollbarGrab, SurfaceRaised);
        Set(ImGuiCol.ScrollbarGrabHovered, Lighten(SurfaceRaised, 0.10f));
        Set(ImGuiCol.ScrollbarGrabActive, Accent);

        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, Lighten(Accent, 0.15f));

        Set(ImGuiCol.Button, SurfaceRaised);
        Set(ImGuiCol.ButtonHovered, Lighten(SurfaceRaised, 0.10f));
        Set(ImGuiCol.ButtonActive, Accent);

        Set(ImGuiCol.Header, SurfaceRaised);
        Set(ImGuiCol.HeaderHovered, Lighten(SurfaceRaised, 0.10f));
        Set(ImGuiCol.HeaderActive, Fade(Accent, 0.70f));

        Set(ImGuiCol.Separator, Border);
        Set(ImGuiCol.SeparatorHovered, Accent);
        Set(ImGuiCol.SeparatorActive, Accent);

        Set(ImGuiCol.ResizeGrip, Transparent);
        Set(ImGuiCol.ResizeGripHovered, Fade(Accent, 0.50f));
        Set(ImGuiCol.ResizeGripActive, Accent);

        Set(ImGuiCol.Tab, Surface);
        Set(ImGuiCol.TabHovered, SurfaceRaised);
        Set(ImGuiCol.TabSelected, SurfaceRaised);
        Set(ImGuiCol.TabSelectedOverline, Accent);
        Set(ImGuiCol.TabDimmed, Background);
        Set(ImGuiCol.TabDimmedSelected, Surface);
        Set(ImGuiCol.TabDimmedSelectedOverline, Fade(Accent, 0.40f));

        Set(ImGuiCol.DockingPreview, Fade(Accent, 0.45f));
        Set(ImGuiCol.DockingEmptyBg, Background);

        Set(ImGuiCol.PlotLines, TextMuted);
        Set(ImGuiCol.PlotLinesHovered, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotHistogramHovered, Lighten(Accent, 0.15f));

        Set(ImGuiCol.TableHeaderBg, Surface);
        Set(ImGuiCol.TableBorderStrong, Border);
        Set(ImGuiCol.TableBorderLight, Fade(Border, 0.50f));
        Set(ImGuiCol.TableRowBg, Transparent);
        Set(ImGuiCol.TableRowBgAlt, Fade(Surface, 0.40f));

        Set(ImGuiCol.TextSelectedBg, Fade(Accent, 0.35f));
        Set(ImGuiCol.NavCursor, Accent);
        Set(ImGuiCol.DragDropTarget, Accent);
        // A dimmed background exists so a modal reads as modal; at full strength it hides the work
        // the modal is about.
        Set(ImGuiCol.ModalWindowDimBg, new Vector4(0f, 0f, 0f, 0.45f));
    }

    private static readonly Vector4 Transparent = new(0f, 0f, 0f, 0f);

    private static void Set(ImGuiCol slot, Vector4 color) => ImGuiApi.GetStyle().Colors[(int)slot] = color;

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static Vector4 Lighten(Vector4 color, float amount) =>
        new(
            Math.Min(color.X + amount, 1f),
            Math.Min(color.Y + amount, 1f),
            Math.Min(color.Z + amount, 1f),
            color.W);

    private static Vector4 Fade(Vector4 color, float alpha) => color with { W = alpha };
}

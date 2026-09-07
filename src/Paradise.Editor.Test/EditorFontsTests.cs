using System.Numerics;
using System.Reflection;
using Paradise.Editor.ImGui;
using Paradise.Ui.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.Test;

/// <summary>The embedded faces, and the two ways they silently stop working.</summary>
/// <remarks>Both failures here are quiet by nature: a CFF font makes stb_truetype assert somewhere
/// that names neither the font nor the editor, and an icon constant added without re-subsetting
/// renders as a blank box that looks like a styling problem. Neither shows up in a build.</remarks>
[NotInParallel]
public class EditorFontsTests
{
    private static IReadOnlyDictionary<string, string> IconMap()
    {
        using var stream = typeof(EditorFonts).Assembly
            .GetManifestResourceStream("Paradise.Editor.ImGui.Fonts.icon-map.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' '))
            .ToDictionary(parts => parts[0], parts => parts[1]);
    }

    // The engine's own gate. Hexa's cimgui rasterizes through stb_truetype, which asserts on CFF
    // outlines, so an .otf slipping in here would fail at load rather than at build.
    [Test]
    public async Task both_embedded_faces_are_fonts_stb_truetype_can_read()
    {
        using var fonts = EditorFonts.Mount();

        await Assert.That(UiFonts.IsStbLoadableTrueType(fonts, EditorFonts.Inter)).IsTrue();
        await Assert.That(UiFonts.IsStbLoadableTrueType(fonts, EditorFonts.Icons)).IsTrue();
    }

    [Test]
    public async Task the_icon_constants_match_the_map_the_font_was_subset_from()
    {
        var map = IconMap();
        var constants = typeof(EditorIcons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

        await Assert.That(constants.Count).IsEqualTo(map.Count);
        foreach (var (name, codepoint) in map)
        {
            var expected = string.Concat(name.Split('_').Select(part =>
                char.ToUpperInvariant(part[0]) + part[1..]));
            await Assert.That(constants).ContainsKey(expected);
            await Assert.That((int)constants[expected][0]).IsEqualTo(Convert.ToInt32(codepoint, 16));
        }
    }

    // The Private Use Area is what makes merging safe: a merged glyph is only taken where the base
    // font has none, so an icon outside the PUA could displace a letter.
    [Test]
    public async Task every_icon_is_a_single_char_in_the_private_use_area()
    {
        foreach (var field in typeof(EditorIcons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var glyph = (string)field.GetRawConstantValue()!;
            await Assert.That(glyph.Length).IsEqualTo(1);
            await Assert.That((int)glyph[0]).IsGreaterThanOrEqualTo(0xE000);
            await Assert.That((int)glyph[0]).IsLessThanOrEqualTo(0xF8FF);
        }
    }

    [Test]
    public async Task the_editor_font_loads_into_a_context()
    {
        using var context = new EditorImGuiContext(addDefaultFont: false);

        await Assert.That(EditorFonts.Load()).IsTrue();
    }

    // A zero-initialised ImFontConfig is not a default-constructed one: ImGui's own constructor
    // sets GlyphMaxAdvanceX to FLT_MAX, and left at zero every glyph advances by nothing, so the
    // whole UI renders as one column of overlapping marks. It builds, loads and reports success —
    // only a person looking at a frame, or this, catches it.
    [Test]
    public async Task loaded_text_advances_instead_of_stacking_on_itself()
    {
        using var context = new EditorImGuiContext(addDefaultFont: false);
        EditorFonts.Load();

        Vector2 one = default;
        Vector2 ten = default;
        Vector2 icon = default;
        context.Frame(() =>
        {
            one = ImGuiApi.CalcTextSize("M");
            ten = ImGuiApi.CalcTextSize("MMMMMMMMMM");
            icon = ImGuiApi.CalcTextSize(EditorIcons.Folder);
        });

        await Assert.That(one.X).IsGreaterThan(0f);
        await Assert.That(ten.X).IsGreaterThan(one.X * 5f);
        await Assert.That(icon.X).IsGreaterThan(0f);
    }
}

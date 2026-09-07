using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Paradise.Ui.ImGui;
using Zio;
using Zio.FileSystems;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui;

/// <summary>The editor's typeface: Inter for text with Material Symbols merged over it, and a
/// system CJK font merged after both when one exists.</summary>
/// <remarks>
/// <para>
/// One ImGui font covering all three, not three fonts, so a label and the icon beside it share a
/// baseline and need no font switch between them. Merging only fills glyphs the base font lacks,
/// which is why the icons live in the Private Use Area — nothing they carry can displace text.
/// </para>
/// <para>
/// The two committed faces are embedded resources rather than files beside the executable, because
/// an editor that will not start when someone moves the binary is not shipping a font, it is
/// shipping a dependency. They are handed to ImGui through a <see cref="MemoryFileSystem"/> for
/// the same reason everything else here takes a mount: <see cref="UiFonts.TryAddFont"/> reads from
/// one, and inventing a second path for "bytes I already have" would duplicate the allocator
/// pairing it exists to get right.
/// </para>
/// <para>
/// CJK is NOT embedded. Inter has no CJK coverage and a font that did would be tens of megabytes;
/// the system font is merged when <see cref="UiFonts.FindCjkFont"/> finds one, which is what the
/// host already did before any of this was embedded. Under the 1.92 texture protocol glyphs
/// rasterize on demand, so a CJK face costs nothing until CJK text is actually drawn.
/// </para>
/// </remarks>
public static class EditorFonts
{
    public const float DefaultSizePixels = 16f;

    private const string InterResource = "Paradise.Editor.ImGui.Fonts.Inter-Regular.ttf";
    private const string IconResource = "Paradise.Editor.ImGui.Fonts.MaterialSymbolsRounded-Editor.ttf";

    /// <summary>The embedded faces, as a mount. The caller owns it and must keep it alive only
    /// until <see cref="Load"/> returns — ImGui copies the bytes into its own allocation.</summary>
    public static MemoryFileSystem Mount()
    {
        var fonts = new MemoryFileSystem();
        fonts.CreateDirectory("/fonts");
        Extract(fonts, InterResource, Inter);
        Extract(fonts, IconResource, Icons);
        return fonts;
    }

    public static UPath Inter => "/fonts/Inter-Regular.ttf";

    public static UPath Icons => "/fonts/MaterialSymbolsRounded-Editor.ttf";

    /// <summary>The text face, as the config a host hands to <c>ImGuiUiCore</c>.</summary>
    /// <remarks>The BASE font has to be added first, because ImGui treats the first font in the
    /// atlas as the default one — added second it would load correctly and never be used.</remarks>
    public static UiFontConfig Base(IFileSystem fonts, float sizePixels = DefaultSizePixels) =>
        new(fonts, Inter, sizePixels);

    /// <summary>Merge the icons onto the font already added.</summary>
    public static bool MergeIcons(IFileSystem fonts, float sizePixels = DefaultSizePixels) =>
        UiFonts.TryAddFont(ImGuiApi.GetIO(), new UiFontConfig(fonts, Icons, sizePixels)
        {
            Merge = true,
            // Icons are drawn from a taller box than text, so they ride high without this; the
            // offset is what puts them on the text baseline rather than above it.
            GlyphOffset = new Vector2(0f, 3f),
            // Uniform advance, so a column of icons down a hierarchy lines up whichever glyph
            // each row happens to use.
            GlyphMinAdvanceX = sizePixels,
        });

    /// <summary>Merge a system CJK face when <paramref name="systemFonts"/> holds one.</summary>
    public static bool MergeSystemCjk(IFileSystem? systemFonts, float sizePixels = DefaultSizePixels) =>
        systemFonts is not null
        && UiFonts.FindCjkFont(systemFonts, sizePixels) is { } cjk
        && UiFonts.TryAddFont(ImGuiApi.GetIO(), cjk with { Merge = true });

    /// <summary>Build the editor's font into the current ImGui context. Call once, after the
    /// context exists and before the first frame.</summary>
    /// <param name="sizePixels">Body size. Icons are added at the same size so they match the
    /// line they sit on.</param>
    /// <param name="systemFonts">Where to look for a CJK face, or null to skip. The standalone
    /// host passes <see cref="UiFonts.MountSystemFonts"/>; a test passes nothing.</param>
    /// <returns>Whether the embedded text face loaded. False means ImGui is on its built-in
    /// ASCII font and the editor is still usable, which is the right outcome for a font problem.</returns>
    public static unsafe bool Load(float sizePixels = DefaultSizePixels, IFileSystem? systemFonts = null)
    {
        using var embedded = Mount();
        var io = ImGuiApi.GetIO();

        if (!UiFonts.TryAddFont(io, Base(embedded, sizePixels)))
        {
            io.Fonts.AddFontDefault();
            return false;
        }

        MergeIcons(embedded, sizePixels);
        MergeSystemCjk(systemFonts, sizePixels);
        return true;
    }

    private static void Extract(IFileSystem target, string resource, UPath path)
    {
        using var stream = typeof(EditorFonts).GetTypeInfo().Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"'{resource}' is not embedded in this assembly.");
        using var file = target.CreateFile(path);
        stream.CopyTo(file);
    }
}

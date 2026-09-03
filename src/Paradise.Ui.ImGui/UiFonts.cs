using System;
using System.Buffers.Binary;
using System.IO;
using Hexa.NET.ImGui;

namespace Paradise.Ui.ImGui;

/// <summary>Which font <see cref="ImGuiUiCore"/> loads. An empty or null
/// <paramref name="Path"/> means "probe the platform's known CJK-capable system fonts".
///
/// There is no glyph-range parameter, and that is the 1.92 texture protocol paying for itself:
/// glyphs rasterize on demand and the atlas grows to fit, so nothing has to be declared up
/// front. The pre-1.92 core took the game's whole content as a string to bake ranges from.</summary>
/// <param name="Path">A TrueType file, or null/empty to probe the system.</param>
/// <param name="SizePixels">Rasterization size in pixels.</param>
public sealed record UiFontConfig(string? Path, float SizePixels);

/// <summary>
/// Font resolution for <see cref="ImGuiUiCore"/>. ImGui's default font is ASCII-only, so any CJK
/// text renders as '?'; loading a system font fixes that.
///
/// The catch, and the reason this file still exists at all: Hexa's cimgui natives are built with
/// stb_truetype and no ImGuiFreeType, and stb only parses TrueType ('glyf') outlines — feeding
/// it a CFF/OpenType font (Hiragino, Noto CJK OTC) asserts inside native code where there is
/// nothing to catch. So candidates are sniffed by container magic first and CFF fonts are
/// skipped; a font that fails the sniff falls back to the next candidate, and no candidate at all
/// falls back to ImGui's default font.
/// </summary>
public static class UiFonts
{
    /// <summary>Well-known CJK-capable system fonts per platform, tried in order. All are
    /// verified TrueType by <see cref="IsStbLoadableTrueType"/> before use anyway.</summary>
    public static readonly string[] SystemCjkFontCandidates =
    [
        // macOS
        "/Library/Fonts/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/STHeiti Light.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        // Windows
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\simhei.ttf",
        @"C:\Windows\Fonts\simsun.ttc",
        // Linux
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
    ];

    /// <summary>True when the file exists and its first face uses TrueType outlines that
    /// stb_truetype can parse (sfnt 0x00010000 / 'true'; for 'ttcf' collections the first face is
    /// checked). 'OTTO' (CFF) and anything unreadable is rejected.</summary>
    public static bool IsStbLoadableTrueType(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header[..4]) != 4) return false;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);

            if (tag == 0x74746366) // 'ttcf' — check the first face's sfnt version
            {
                if (stream.Read(header[..12]) != 12) return false;
                var firstFaceOffset = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
                stream.Position = firstFaceOffset;
                if (stream.Read(header[..4]) != 4) return false;
                tag = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            }

            return tag is 0x00010000 or 0x74727565; // sfnt v1 / 'true'
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The first stb-loadable CJK system font on this machine, or null.</summary>
    public static string? FindSystemCjkFont()
    {
        foreach (var candidate in SystemCjkFontCandidates)
        {
            if (File.Exists(candidate) && IsStbLoadableTrueType(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Resolve <paramref name="font"/> to a usable file and add it to the atlas. False =
    /// nothing added and the caller should fall back to ImGui's default font.</summary>
    internal static unsafe bool TryAddFont(ImGuiIOPtr io, UiFontConfig font)
    {
        var path = string.IsNullOrWhiteSpace(font.Path) ? FindSystemCjkFont() : font.Path;
        if (path is null || !File.Exists(path) || !IsStbLoadableTrueType(path)) return false;

        return io.Fonts.AddFontFromFileTTF(path, font.SizePixels) is not null;
    }
}

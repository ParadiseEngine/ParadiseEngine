using System;
using System.Buffers.Binary;
using System.IO;
using Hexa.NET.ImGui;
using Zio;

namespace Paradise.Ui.ImGui;

/// <summary>Which font <see cref="ImGuiUiCore"/> loads, and the mount it lives in.
///
/// There is no glyph-range parameter, and that is the 1.92 texture protocol paying for itself:
/// glyphs rasterize on demand and the atlas grows to fit, so nothing has to be declared up
/// front. The pre-1.92 core took the game's whole content as a string to bake ranges from.</summary>
/// <param name="Content">The mount the font file lives in — a host mounts its content tree, a
/// test mounts memory. See <see cref="UiFonts.FindSystemCjkFont"/> for the one case that starts
/// from a host path.</param>
/// <param name="Path">The font file, as a path in <paramref name="Content"/>.</param>
/// <param name="SizePixels">Rasterization size in pixels.</param>
public sealed record UiFontConfig(IFileSystem Content, UPath Path, float SizePixels);

/// <summary>
/// Font resolution for <see cref="ImGuiUiCore"/>. ImGui's default font is ASCII-only, so any CJK
/// text renders as '?'; loading a real font fixes that.
///
/// The catch, and the reason this file still exists at all: Hexa's cimgui natives are built with
/// stb_truetype and no ImGuiFreeType, and stb only parses TrueType ('glyf') outlines — feeding it
/// a CFF/OpenType font (Hiragino, Noto CJK OTC) asserts inside native code where there is nothing
/// to catch. So a candidate is sniffed by container magic first and CFF is refused; a font that
/// fails the sniff falls back to ImGui's default font.
/// </summary>
public static class UiFonts
{
    /// <summary>Well-known CJK-capable system fonts per platform, tried in order by
    /// <see cref="FindSystemCjkFont"/>. HOST paths, not mount paths — see that method.</summary>
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

    /// <summary>True when <paramref name="path"/> exists in <paramref name="content"/> and its
    /// first face uses TrueType outlines stb_truetype can parse (sfnt 0x00010000 / 'true'; for a
    /// 'ttcf' collection the first face is checked). 'OTTO' (CFF) and anything unreadable is
    /// refused.
    ///
    /// Reads the header only. A CJK font is tens of megabytes and most candidates are rejected,
    /// so the sniff does not pay to load one.</summary>
    public static bool IsStbLoadableTrueType(IFileSystem content, UPath path)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            if (!content.FileExists(path)) return false;
            // FileShare.Read explicitly: Zio's three-argument OpenFile defaults the share to
            // None, which turns a second concurrent open of a system font into an IOException.
            using var stream = content.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    /// <summary>The first stb-loadable CJK font among the platform's well-known SYSTEM fonts, as
    /// a config ready to hand to <see cref="ImGuiUiCore"/>. Null when this machine has none.
    ///
    /// <b>This is the one place here that starts from a host path, and it has to.</b> The OS font
    /// directory is not in a game's content mount and never will be, so the candidates are host
    /// paths that <paramref name="host"/> translates. Everything downstream — the sniff, the read —
    /// still goes through the mount, which is why <paramref name="host"/> is passed in rather than
    /// created: whatever this font is loaded through stays the host's to own and dispose.</summary>
    /// <param name="host">A mount over the real filesystem (a <c>PhysicalFileSystem</c>), used to
    /// translate a host path into one of its own.</param>
    /// <param name="sizePixels">Rasterization size for the returned config.</param>
    public static UiFontConfig? FindSystemCjkFont(IFileSystem host, float sizePixels)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (var candidate in SystemCjkFontCandidates)
        {
            var path = host.ConvertPathFromInternal(candidate);
            if (IsStbLoadableTrueType(host, path)) return new UiFontConfig(host, path, sizePixels);
        }
        return null;
    }

    /// <summary>Read <paramref name="font"/> out of its mount and add it to the atlas. False =
    /// nothing added and the caller should fall back to ImGui's default font.</summary>
    internal static unsafe bool TryAddFont(ImGuiIOPtr io, UiFontConfig font)
    {
        if (!IsStbLoadableTrueType(font.Content, font.Path)) return false;

        byte[] bytes;
        try
        {
            using var stream = font.Content.OpenFile(font.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (IOException)
        {
            return false;
        }

        // ImGui's OWN allocator, not Marshal's. AddFontFromMemoryTTF defaults to
        // FontDataOwnedByAtlas, so the atlas frees this buffer with IM_FREE when the context goes
        // away — and it must, because 1.92 rasterizes glyphs on demand and re-reads the font data
        // for the whole life of the atlas, so the bytes cannot be a managed array we let go of.
        // Pairing IM_FREE with anything but ImGui.MemAlloc is undefined.
        var buffer = Hexa.NET.ImGui.ImGui.MemAlloc((nuint)bytes.Length);
        if (buffer is null) return false;
        bytes.AsSpan().CopyTo(new Span<byte>(buffer, bytes.Length));
        return io.Fonts.AddFontFromMemoryTTF(buffer, bytes.Length, font.SizePixels) is not null;
    }
}

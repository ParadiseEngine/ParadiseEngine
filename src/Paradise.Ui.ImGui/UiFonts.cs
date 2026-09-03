using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui;

/// <summary>Which font <see cref="ImGuiUiCore"/> loads, and the mount it is read out of.
///
/// There is no glyph-range parameter, and that is the 1.92 texture protocol paying for itself:
/// glyphs rasterize on demand and the atlas grows to fit, so nothing has to be declared up
/// front. The pre-1.92 core took the game's whole content as a string to bake ranges from.</summary>
/// <param name="Content">The mount the font file lives in — a game's own font directory, the
/// system fonts from <see cref="UiFonts.MountSystemFonts"/>, or both overlaid.</param>
/// <param name="Path">The font file, as a path in <paramref name="Content"/>.</param>
/// <param name="SizePixels">Rasterization size in pixels.</param>
public sealed record UiFontConfig(IFileSystem Content, UPath Path, float SizePixels);

/// <summary>
/// Font resolution for <see cref="ImGuiUiCore"/>. ImGui's default font is ASCII-only, so any CJK
/// text renders as '?'; loading a real font fixes that.
///
/// <b>Fonts are looked up BY NAME in a mount the caller composed</b>, which is what lets a game
/// ship its own font and have it shadow the system copy with no second code path (see
/// <see cref="MountSystemFonts"/>). The only host paths left here are the platform's font
/// DIRECTORIES, and they appear once, at the boundary where a mount is built.
///
/// The catch, and why a curated name list survives at all: nothing in a mount says which font can
/// draw 中. <see cref="IsStbLoadableTrueType"/> answers whether stb_truetype can PARSE the file,
/// not what it covers, so enumerating a font directory and taking the first loadable entry yields
/// Arial and renders every CJK codepoint as a box. Answering coverage properly means parsing the
/// font's <c>cmap</c> — a real binary parser, and a separate change; until then
/// <see cref="CjkFontFileNames"/> is the table that knows.
///
/// The parse check is not optional either: Hexa's cimgui natives are built with stb_truetype and
/// no ImGuiFreeType, and stb reads TrueType ('glyf') outlines only — handing it a CFF/OpenType
/// font (Hiragino, Noto CJK OTC) asserts inside native code where nothing can catch it.
/// </summary>
public static class UiFonts
{
    /// <summary>Where each platform keeps fonts. HOST paths — the last ones in this file, and
    /// turning them into a mount at one point rather than leaking them through the font API is
    /// what <see cref="MountSystemFonts"/> is for.</summary>
    public static readonly string[] SystemFontDirectories =
    [
        // Windows. The per-user directory is where a font installed without admin rights lands.
        @"C:\Windows\Fonts",
        @"%LOCALAPPDATA%\Microsoft\Windows\Fonts",
        // macOS
        "/System/Library/Fonts",
        "/System/Library/Fonts/Supplemental",
        "/Library/Fonts",
        "~/Library/Fonts",
        // Linux
        "/usr/share/fonts",
        "/usr/local/share/fonts",
        "~/.local/share/fonts",
        "~/.fonts",
    ];

    /// <summary>Font files known to carry CJK coverage, in preference order. NAMES, not paths:
    /// they resolve inside whatever mount is searched, so one list serves the system fonts, a
    /// game's own font directory, or an overlay of both.</summary>
    public static readonly string[] CjkFontFileNames =
    [
        // Windows
        "msyh.ttc",
        "simhei.ttf",
        "simsun.ttc",
        // macOS
        "PingFang.ttc",
        "STHeiti Medium.ttc",
        "STHeiti Light.ttc",
        "Arial Unicode.ttf",
        // Ships with recent Windows and is packaged on most Linux distributions.
        "NotoSansSC-VF.ttf",
        "NotoSansCJK-Regular.ttc",
        // Linux
        "wqy-microhei.ttc",
        "DroidSansFallbackFull.ttf",
    ];

    /// <summary>The platform's font directories as one read-only overlay, ready to search or to
    /// build on: add a game's own font directory with
    /// <see cref="AggregateFileSystem.AddFileSystem"/> and it takes priority, so a shipped font
    /// shadows a system font of the same name.
    ///
    /// Directories this machine does not have are skipped, so the result is only as deep as the
    /// platform actually is. The caller owns what comes back; disposing it disposes the
    /// sub-mounts made here and never <paramref name="host"/>.</summary>
    /// <param name="host">A mount over the real filesystem (a <c>PhysicalFileSystem</c>), which
    /// translates the host paths in <see cref="SystemFontDirectories"/> into paths of its
    /// own.</param>
    public static AggregateFileSystem MountSystemFonts(IFileSystem host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var fonts = new AggregateFileSystem(owned: true);
        foreach (var directory in SystemFontDirectories)
        {
            if (Expand(directory) is not { } expanded) continue;
            var path = host.ConvertPathFromInternal(expanded);
            if (!host.DirectoryExists(path)) continue;
            fonts.AddFileSystem(new SubFileSystem(host, path, owned: false));
        }
        return fonts;
    }

    /// <summary>A directory from <see cref="SystemFontDirectories"/> with <c>%VAR%</c> and a
    /// leading <c>~</c> resolved, or null when this platform cannot resolve it — an unexpanded
    /// <c>%</c> is not a directory name anywhere we target, so it is skipped rather than handed
    /// to the mount to reject.</summary>
    private static string? Expand(string directory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(directory);
        if (expanded.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            expanded = home + expanded[1..];
        }
        return expanded.Contains('%') ? null : expanded;
    }

    /// <summary>The first font from <see cref="CjkFontFileNames"/> that <paramref name="fonts"/>
    /// actually provides and stb can parse, or null.
    ///
    /// Each name is looked up at the root first, which is the whole cost on a flat mount (Windows
    /// and macOS keep fonts in one directory). Only if none matches does it walk — Linux nests
    /// fonts under <c>truetype/&lt;family&gt;/</c> — and the walk reads names, never contents.</summary>
    public static UiFontConfig? FindCjkFont(IFileSystem fonts, float sizePixels)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        foreach (var name in CjkFontFileNames)
        {
            var path = UPath.Combine("/", name);
            if (IsStbLoadableTrueType(fonts, path)) return new UiFontConfig(fonts, path, sizePixels);
        }

        var nested = new Dictionary<string, UPath>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fonts.EnumerateFiles("/", "*", SearchOption.AllDirectories))
        {
            var name = file.GetName();
            if (!nested.ContainsKey(name) &&
                Array.Exists(CjkFontFileNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
            {
                nested[name] = file;
            }
        }
        // Preference comes from the list, not from wherever the walk happened to reach first.
        foreach (var name in CjkFontFileNames)
        {
            if (nested.TryGetValue(name, out var path) && IsStbLoadableTrueType(fonts, path))
            {
                return new UiFontConfig(fonts, path, sizePixels);
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="path"/> exists in <paramref name="content"/> and its
    /// first face uses TrueType outlines stb_truetype can parse (sfnt 0x00010000 / 'true'; for a
    /// 'ttcf' collection the first face is checked). 'OTTO' (CFF) and anything unreadable is
    /// refused.
    ///
    /// Reads the header only. A CJK font is tens of megabytes and most candidates are refused, so
    /// the sniff does not pay to load one.</summary>
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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui;

/// <summary>Selects a font file and rasterization size for ImGuiUiCore.</summary>
/// <remarks>ImGui 1.92 rasterizes glyphs on demand, so no glyph ranges need to be
/// declared.</remarks>
/// <param name="Content">The mount the font file lives in — a game's own font directory, the
/// system fonts from <see cref="UiFonts.MountSystemFonts"/>, or both overlaid.</param>
/// <param name="Path">The font file, as a path in <paramref name="Content"/>.</param>
/// <param name="SizePixels">Rasterization size in pixels.</param>
public sealed record UiFontConfig(IFileSystem Content, UPath Path, float SizePixels);

/// <summary>Resolves CJK-capable TrueType fonts through caller-supplied mounts.</summary>
/// <remarks>CjkFontFileNames supplies coverage knowledge; header validation checks format only.
/// Reject CFF/OpenType outlines before stb_truetype can assert in native code. Host paths are used
/// only when mounting system font directories.</remarks>
public static class UiFonts
{
    /// <summary>Lists this platform's font directories in overlay order.</summary>
    /// <remarks>Foreign-platform paths can fail Zio conversion rather than simply report a missing
    /// directory.</remarks>
    public static readonly string[] SystemFontDirectories = BuildSystemFontDirectories();

    private static string[] BuildSystemFontDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                // Asked for rather than spelled: the Windows directory is not always on C:.
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                // Where a font installed without admin rights lands.
                @"%LOCALAPPDATA%\Microsoft\Windows\Fonts",
            ];
        }
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/System/Library/Fonts",
                "/System/Library/Fonts/Supplemental",
                "/Library/Fonts",
                "~/Library/Fonts",
            ];
        }
        return
        [
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            "~/.local/share/fonts",
            "~/.fonts",
        ];
    }

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

    /// <summary>Mounts existing system font directories as a caller-owned overlay.</summary>
    /// <remarks>Adding a game font mount gives it priority. Disposing the overlay disposes its
    /// submounts but never the host filesystem.</remarks>
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
            UPath path;
            try
            {
                path = host.ConvertPathFromInternal(expanded);
            }
            catch (ArgumentException)
            {
                // A path this mount cannot express is "no such font directory", not a fault:
                // ConvertPathFromInternal throws rather than returning nothing for a path shaped
                // for another platform. SystemFontDirectories already avoids the case, so this
                // only catches a host that mounted something other than the real filesystem.
                continue;
            }
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
        // GetFolderPath answers "" for a folder this platform does not have.
        if (string.IsNullOrEmpty(directory)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(directory);
        if (expanded.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            expanded = home + expanded[1..];
        }
        return expanded.Contains('%') ? null : expanded;
    }

    /// <summary>Finds the first available, stb-loadable font in CjkFontFileNames order.</summary>
    /// <remarks>Checks root files first, then searches nested filenames for platforms that organize
    /// fonts by family.</remarks>
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
            if (Array.Exists(CjkFontFileNames, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
            {
                nested.TryAdd(name, file);
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

    /// <summary>Checks the first font face's header for stb-compatible TrueType outlines.</summary>
    /// <remarks>Accepts sfnt 0x00010000 and true, including the first face of ttcf collections;
    /// rejects CFF/OTTO and unreadable files without loading the full font.</remarks>
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

        // Use ImGui.MemAlloc: the atlas retains font bytes for on-demand glyphs and releases them
        // with IM_FREE at context destruction.
        var buffer = Hexa.NET.ImGui.ImGui.MemAlloc((nuint)bytes.Length);
        if (buffer is null) return false;
        bytes.AsSpan().CopyTo(new Span<byte>(buffer, bytes.Length));
        if (io.Fonts.AddFontFromMemoryTTF(buffer, bytes.Length, font.SizePixels) is not null) return true;
        // Refused: the atlas never took ownership, so this is the one path where the pairing
        // above is ours to complete.
        Hexa.NET.ImGui.ImGui.MemFree(buffer);
        return false;
    }
}

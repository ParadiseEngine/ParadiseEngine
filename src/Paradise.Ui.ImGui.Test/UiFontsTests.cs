using System;
using System.Collections.Generic;
using Zio;
using Zio.FileSystems;

namespace Paradise.Ui.ImGui.Test;

/// <summary>Font resolution against a mount rather than a host path — memory for the cases that
/// only need bytes, the real filesystem for the system-font probe that cannot avoid one.
///
/// The sniff is the load-bearing part: Hexa's natives rasterize with stb_truetype, which parses
/// TrueType outlines only, and handing it a CFF font asserts inside native code where no test can
/// catch it. Every one of these files would have had to exist on disk before the mount.
///
/// Serialized with the rest of the ImGui suites: two of these build a real core, and the current
/// ImGui context is process-global.</summary>
[NotInParallel]
public class UiFontsTests
{
    /// <summary>An sfnt header with <paramref name="tag"/> as its version, padded out so a reader
    /// that expects a table directory has something to read.</summary>
    private static byte[] Sfnt(uint tag)
    {
        var bytes = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, tag);
        return bytes;
    }

    /// <summary>A 'ttcf' collection whose first face carries <paramref name="firstFaceTag"/> —
    /// the offset to that face is the field at byte 12 the sniff has to follow.</summary>
    private static byte[] Collection(uint firstFaceTag)
    {
        const int firstFaceOffset = 32;
        var bytes = new byte[64];
        var span = bytes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span, 0x74746366u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span[12..], firstFaceOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span[firstFaceOffset..], firstFaceTag);
        return bytes;
    }

    private static MemoryFileSystem Mount(string path, byte[] content)
    {
        var fs = new MemoryFileSystem();
        fs.CreateDirectory(((UPath)path).GetDirectory());
        fs.WriteAllBytes(path, content);
        return fs;
    }

    [Test]
    [Arguments(0x00010000u)] // sfnt v1
    [Arguments(0x74727565u)] // 'true'
    public async Task truetype_outlines_are_accepted(uint tag)
    {
        using var fs = Mount("/fonts/font.ttf", Sfnt(tag));
        await Assert.That(UiFonts.IsStbLoadableTrueType(fs, "/fonts/font.ttf")).IsTrue();
    }

    [Test]
    public async Task a_cff_font_is_refused_rather_than_handed_to_stb()
    {
        using var fs = Mount("/fonts/font.otf", Sfnt(0x4F54544Fu)); // 'OTTO'
        await Assert.That(UiFonts.IsStbLoadableTrueType(fs, "/fonts/font.otf")).IsFalse();
    }

    [Test]
    public async Task a_collection_is_judged_by_its_first_face()
    {
        using var cff = Mount("/fonts/cff.ttc", Collection(firstFaceTag: 0x4F54544Fu));
        await Assert.That(UiFonts.IsStbLoadableTrueType(cff, "/fonts/cff.ttc")).IsFalse();

        using var trueType = Mount("/fonts/tt.ttc", Collection(firstFaceTag: 0x00010000u));
        await Assert.That(UiFonts.IsStbLoadableTrueType(trueType, "/fonts/tt.ttc")).IsTrue();
    }

    [Test]
    public async Task a_missing_or_truncated_file_is_refused_without_throwing()
    {
        using var fs = Mount("/fonts/stub.ttf", new byte[2]);
        await Assert.That(UiFonts.IsStbLoadableTrueType(fs, "/fonts/stub.ttf")).IsFalse();
        await Assert.That(UiFonts.IsStbLoadableTrueType(fs, "/fonts/absent.ttf")).IsFalse();
    }

    [Test]
    public async Task a_font_that_fails_the_sniff_leaves_the_core_on_the_default_font()
    {
        using var fs = Mount("/fonts/font.otf", Sfnt(0x4F54544Fu));
        // Nothing here may reach stb: a CFF font that got through would abort the test process
        // inside native code, so "the core still ticks" is the whole assertion.
        using var core = new ImGuiUiCore(320, 240, new UiFontConfig(fs, "/fonts/font.otf", 18f));
        core.AddDraw(() => ImGuiTestContext.Panel("hello"));

        core.Input.Tick(0.0);
        var ops = new List<ImGuiTextureOp>();
        var snapshot = core.AcquireSnapshotForRender(ops, out _);

        await Assert.That(snapshot!.CommandCount).IsGreaterThan(0);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);
    }

    /// <summary>Lookup is by NAME in the mount, so a font the caller overlays wins over the
    /// system copy — this is the whole reason the search moved off absolute paths.</summary>
    [Test]
    public async Task an_overlaid_font_shadows_a_system_font_of_the_same_name()
    {
        var name = UiFonts.CjkFontFileNames[0];
        using var system = new MemoryFileSystem();
        system.WriteAllBytes(UPath.Combine("/", name), Sfnt(0x00010000u));
        using var game = new MemoryFileSystem();
        game.WriteAllBytes(UPath.Combine("/", name), Sfnt(0x74727565u));

        using var fonts = new AggregateFileSystem(owned: false);
        fonts.AddFileSystem(system);
        fonts.AddFileSystem(game); // last added wins

        var found = UiFonts.FindCjkFont(fonts, 18f);
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Path.GetName()).IsEqualTo(name);
        // The overlay decided which bytes answer, not the layer order of the paths.
        await Assert.That(fonts.FindFirstFileSystemEntry(found.Path)!.FileSystem).IsSameReferenceAs(game);
    }

    /// <summary>Linux nests fonts under <c>truetype/&lt;family&gt;/</c>, so a name that is not at
    /// the root still has to be found — and preference must come from the list rather than from
    /// wherever the walk reached first.</summary>
    [Test]
    public async Task a_nested_font_is_found_and_the_list_still_sets_preference()
    {
        var preferred = UiFonts.CjkFontFileNames[0];
        var alsoPresent = UiFonts.CjkFontFileNames[^1];
        using var fonts = new MemoryFileSystem();
        fonts.CreateDirectory("/truetype/wqy");
        fonts.CreateDirectory("/truetype/aaa");
        // The less-preferred name sits in the directory a walk reaches first.
        fonts.WriteAllBytes(UPath.Combine("/truetype/aaa", alsoPresent), Sfnt(0x00010000u));
        fonts.WriteAllBytes(UPath.Combine("/truetype/wqy", preferred), Sfnt(0x00010000u));

        var found = UiFonts.FindCjkFont(fonts, 18f);
        await Assert.That(found!.Path.GetName()).IsEqualTo(preferred);
    }

    [Test]
    public async Task a_mount_with_no_known_cjk_font_reports_nothing()
    {
        using var fonts = Mount("/arial.ttf", Sfnt(0x00010000u));
        await Assert.That(UiFonts.FindCjkFont(fonts, 18f)).IsNull();
    }

    /// <summary>The directory list must hold paths THIS platform's mount can express.
    ///
    /// Regression test: the list used to carry every platform's directories at once, and
    /// Zio is asymmetric about a foreign one — a Unix path on Windows converts to an absolute
    /// path that merely does not exist, while <c>C:\Windows\Fonts</c> on Unix converts to
    /// <c>C:/Windows/Fonts</c>, which is not absolute, so the next call throws. That passed on
    /// Windows and failed on Linux CI.</summary>
    [Test]
    public async Task the_platform_font_directories_can_all_be_expressed_by_a_physical_mount()
    {
        using var host = new PhysicalFileSystem();
        await Assert.That(UiFonts.SystemFontDirectories).IsNotEmpty();
        foreach (var directory in UiFonts.SystemFontDirectories)
        {
            await Assert.That(directory).IsNotNullOrEmpty();
            var expanded = Environment.ExpandEnvironmentVariables(directory);
            if (expanded.StartsWith('~') || expanded.Contains('%')) continue; // resolved later
            await Assert.That(() => host.ConvertPathFromInternal(expanded)).ThrowsNothing();
        }
    }

    /// <summary>Mounting must be total: a host that cannot express a font directory gets an empty
    /// overlay, not an exception.</summary>
    [Test]
    public async Task mounting_system_fonts_over_a_mount_that_cannot_express_them_is_empty()
    {
        using var host = new MemoryFileSystem();
        using var fonts = UiFonts.MountSystemFonts(host);
        await Assert.That(UiFonts.FindCjkFont(fonts, 18f)).IsNull();
    }

    [Test]
    public async Task mounting_system_fonts_yields_a_font_this_machine_can_actually_load()
    {
        using var host = new PhysicalFileSystem();
        using var fonts = UiFonts.MountSystemFonts(host);
        var font = UiFonts.FindCjkFont(fonts, 18f);
        if (font is null)
        {
            Skip.Test("No stb-loadable CJK system font on this machine.");
            return;
        }

        // The search hands back a path in the MOUNT, not the host path a directory started from.
        await Assert.That(font.Content).IsSameReferenceAs((IFileSystem)fonts);
        await Assert.That(font.SizePixels).IsEqualTo(18f);
        await Assert.That(UiFonts.IsStbLoadableTrueType(font.Content, font.Path)).IsTrue();
    }

    /// <summary>The search's own output, loaded for real — the only test that puts a
    /// multi-megabyte system font through stb and asks for a glyph the default font lacks.</summary>
    [Test]
    public async Task a_system_cjk_font_rasterizes_new_glyphs_on_demand()
    {
        using var host = new PhysicalFileSystem();
        using var fonts = UiFonts.MountSystemFonts(host);
        var font = UiFonts.FindCjkFont(fonts, 18f);
        if (font is null)
        {
            Skip.Test("No stb-loadable CJK system font on this machine.");
            return;
        }

        var text = "hello";
        using var core = new ImGuiUiCore(320, 240, font);
        core.AddDraw(() => ImGuiTestContext.Panel(text));
        var ops = new List<ImGuiTextureOp>();

        core.Input.Tick(0.0);
        core.AcquireSnapshotForRender(ops, out _);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Create);

        // CJK the first frame never asked for: with no glyph ranges declared anywhere, these can
        // only appear if 1.92 rasterized them on demand.
        text = "中文測試";
        core.Input.Tick(1.0 / 60.0);
        core.AcquireSnapshotForRender(ops, out _);

        await Assert.That(ops.Count).IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(ImGuiTextureOpKind.Update);
    }
}

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

    [Test]
    public async Task the_system_probe_returns_a_font_this_machine_can_actually_load()
    {
        using var host = new PhysicalFileSystem();
        var font = UiFonts.FindSystemCjkFont(host, 18f);
        if (font is null)
        {
            Skip.Test("No stb-loadable CJK system font on this machine.");
            return;
        }

        // The probe hands back a path in the MOUNT, not the host path it started from.
        await Assert.That(font.Content).IsSameReferenceAs(host);
        await Assert.That(font.SizePixels).IsEqualTo(18f);
        await Assert.That(UiFonts.IsStbLoadableTrueType(font.Content, font.Path)).IsTrue();
    }

    /// <summary>The probe's own output, loaded for real — the only test that puts a multi-megabyte
    /// system font through stb and asks for a glyph it does not have in the default font.</summary>
    [Test]
    public async Task a_system_cjk_font_rasterizes_new_glyphs_on_demand()
    {
        using var host = new PhysicalFileSystem();
        var font = UiFonts.FindSystemCjkFont(host, 18f);
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

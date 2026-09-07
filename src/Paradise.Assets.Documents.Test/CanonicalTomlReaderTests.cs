using Paradise.Assets.Documents;

namespace Paradise.Assets.Documents.Test;

/// <summary>The reader, and the round trip that makes the pair trustworthy.</summary>
/// <remarks>A writer whose output its own reader cannot read is the failure this pins: the
/// canonical form exists so two toolchains agree on bytes, and a caller that saves settings and
/// loads them back is exercising exactly that agreement.</remarks>
public class CanonicalTomlReaderTests
{
    [Test]
    public async Task scalars_and_arrays_round_trip_through_the_writer()
    {
        var written = new CanonicalTomlTable
        {
            { "Name", "editor" },
            { "Version", 3 },
            { "Scale", 1.5f },
            { "Enabled", true },
            { "Position", new object[] { 1.0, 2.5, -3.0 } },
        };

        var read = CanonicalTomlReader.Parse(CanonicalTomlWriter.WriteString(written), "settings.toml");

        await Assert.That(read.Value("Name")).IsEqualTo("editor");
        await Assert.That(read.Value("Version")).IsEqualTo(3L);
        await Assert.That(read.Value("Scale")).IsEqualTo(1.5d);
        await Assert.That(read.Value("Enabled")).IsTypeOf<bool>();
        await Assert.That(CanonicalTomlWriter.WriteString(read)).IsEqualTo(CanonicalTomlWriter.WriteString(written));
    }

    // Order is part of the canonical form, so a reader that returned a hash-ordered map would
    // produce a file that differs from the one it read while representing the same values.
    [Test]
    public async Task key_order_survives_the_round_trip()
    {
        var written = new CanonicalTomlTable { { "Zulu", 1 }, { "Alpha", 2 }, { "Mike", 3 } };

        var read = CanonicalTomlReader.Parse(CanonicalTomlWriter.WriteString(written), "order.toml");

        await Assert.That(read.Select(pair => pair.Key).ToArray()).IsEquivalentTo(new[] { "Zulu", "Alpha", "Mike" });
    }

    [Test]
    public async Task a_nested_table_round_trips()
    {
        var nested = new CanonicalTomlTable { { "Chord", "Ctrl+Z" }, { "Operator", "editor.undo" } };
        var written = new CanonicalTomlTable { { "Version", 1 }, { "Binding", nested } };

        var read = CanonicalTomlReader.Parse(CanonicalTomlWriter.WriteString(written), "keymap.toml");

        await Assert.That(read.Value("Binding")).IsTypeOf<CanonicalTomlTable>();
        await Assert.That(((CanonicalTomlTable)read.Value("Binding")!).Value("Chord")).IsEqualTo("Ctrl+Z");
    }

    [Test]
    public async Task malformed_text_is_refused_and_names_its_source()
    {
        var error = await Assert.That(() => CanonicalTomlReader.Parse("this is not = = toml", "broken.toml"))
            .Throws<InvalidDataException>();
        await Assert.That(error!.Message).Contains("broken.toml");
    }
}

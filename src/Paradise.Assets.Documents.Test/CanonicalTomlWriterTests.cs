using Tomlyn;
using Tomlyn.Model;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// Executable spec of the canonical writing rules. The expected strings here ARE the contract:
/// the Python mirror's tests pin the same bytes, so a change on either side fails somewhere.
/// </summary>
public class CanonicalTomlWriterTests
{
    [Test]
    public async Task an_empty_document_is_zero_bytes()
    {
        await Assert.That(CanonicalTomlWriter.WriteString(new CanonicalTomlTable())).IsEqualTo("");
        await Assert.That(CanonicalTomlWriter.WriteBytes(new CanonicalTomlTable()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task scalars_are_written_in_model_order()
    {
        var document = new CanonicalTomlTable
        {
            { "name", "district" },
            { "schema_version", 1 },
            { "enabled", true },
            { "weight", 2.5 },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "name = \"district\"\nschema_version = 1\nenabled = true\nweight = 2.5\n");
    }

    [Test]
    public async Task scalars_precede_sub_tables_regardless_of_model_order()
    {
        // TOML itself demands this: a "key = value" line after a [header] belongs to the header.
        var document = new CanonicalTomlTable
        {
            { "transform", new CanonicalTomlTable { { "x", 1 } } },
            { "name", "crate" },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "name = \"crate\"\n\n[transform]\nx = 1\n");
    }

    [Test]
    public async Task keys_are_bare_when_possible_and_quoted_otherwise()
    {
        var document = new CanonicalTomlTable
        {
            { "bare_Key-9", 1 },
            { "two words", 2 },
            { "dotted.key", 3 },
            { "", 4 },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "bare_Key-9 = 1\n\"two words\" = 2\n\"dotted.key\" = 3\n\"\" = 4\n");
    }

    [Test]
    public async Task strings_use_basic_escapes_and_uXXXX_for_other_control_characters()
    {
        var document = new CanonicalTomlTable
        {
            { "quote", "say \"hi\"" },
            { "backslash", @"a\b" },
            { "newline", "a\nb" },
            { "tab", "a\tb" },
            { "control", "a\u0001b" },
            { "delete", "a\u007Fb" },
            { "unicode", "café — ok" },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "quote = \"say \\\"hi\\\"\"\n" +
            "backslash = \"a\\\\b\"\n" +
            "newline = \"a\\nb\"\n" +
            "tab = \"a\\tb\"\n" +
            "control = \"a\\u0001b\"\n" +
            "delete = \"a\\u007Fb\"\n" +
            "unicode = \"café — ok\"\n");
    }

    [Test]
    public async Task arrays_are_single_line_with_comma_space()
    {
        var document = new CanonicalTomlTable
        {
            { "ints", new object[] { 1, 2, 3 } },
            { "floats", new object[] { 0.0, 1.5 } },
            { "nested", new object[] { new object[] { 1, 2 }, new object[] { 3, 4 } } },
            { "empty", Array.Empty<object>() },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "ints = [1, 2, 3]\nfloats = [0.0, 1.5]\nnested = [[1, 2], [3, 4]]\nempty = []\n");
    }

    [Test]
    public async Task nested_tables_get_dotted_headers_with_one_blank_line_before_each()
    {
        var transform = new CanonicalTomlTable { { "position", new object[] { 0.0, 0.0, 0.0 } } };
        transform.Add("nested", new CanonicalTomlTable { { "a", 1 } });
        var document = new CanonicalTomlTable
        {
            { "name", "x" },
            { "transform", transform },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "name = \"x\"\n" +
            "\n[transform]\nposition = [0.0, 0.0, 0.0]\n" +
            "\n[transform.nested]\na = 1\n");
    }

    [Test]
    public async Task a_header_at_the_start_of_the_document_gets_no_blank_line()
    {
        var document = new CanonicalTomlTable
        {
            { "import", new CanonicalTomlTable { { "preset", "color" } } },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "[import]\npreset = \"color\"\n");
    }

    [Test]
    public async Task an_empty_table_still_gets_its_header()
    {
        var document = new CanonicalTomlTable
        {
            { "name", "x" },
            { "import", new CanonicalTomlTable() },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "name = \"x\"\n\n[import]\n");
    }

    [Test]
    public async Task arrays_of_tables_write_one_header_per_element_with_element_sub_tables_between()
    {
        var first = new CanonicalTomlTable { { "name", "a" } };
        first.Add("components", new CanonicalTomlTable { { "Health", new CanonicalTomlTable { { "hp", 10 } } } });
        var second = new CanonicalTomlTable { { "name", "b" } };
        var document = new CanonicalTomlTable
        {
            { "schema_version", 1 },
            { "objects", new CanonicalTomlTable[] { first, second } },
        };

        // The second [[objects]] AFTER the first element's sub-tables is the TOML rule: a
        // sub-table header attaches to the latest [[objects]] element above it.
        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "schema_version = 1\n" +
            "\n[[objects]]\nname = \"a\"\n" +
            "\n[objects.components]\n" +
            "\n[objects.components.Health]\nhp = 10\n" +
            "\n[[objects]]\nname = \"b\"\n");
    }

    [Test]
    public async Task quoted_segments_appear_inside_dotted_headers()
    {
        var document = new CanonicalTomlTable
        {
            { "two words", new CanonicalTomlTable { { "a", 1 } } },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "[\"two words\"]\na = 1\n");
    }

    [Test]
    public async Task the_output_is_valid_toml_that_round_trips_through_tomlyn()
    {
        var inner = new CanonicalTomlTable { { "hp", 10L }, { "speed", 1.25 } };
        var element = new CanonicalTomlTable { { "name", "crate" }, { "tags", new object[] { "solid", "static" } } };
        element.Add("components", inner);
        var document = new CanonicalTomlTable
        {
            { "schema_version", 1 },
            { "objects", new CanonicalTomlTable[] { element } },
        };

        var parsed = TomlSerializer.Deserialize<TomlTable>(CanonicalTomlWriter.WriteString(document))!;
        var objects = (TomlTableArray)parsed["objects"];
        var components = (TomlTable)objects[0]["components"];

        await Assert.That((long)parsed["schema_version"]!).IsEqualTo(1L);
        await Assert.That((string)objects[0]["name"]!).IsEqualTo("crate");
        await Assert.That((double)components["speed"]!).IsEqualTo(1.25);
    }
}

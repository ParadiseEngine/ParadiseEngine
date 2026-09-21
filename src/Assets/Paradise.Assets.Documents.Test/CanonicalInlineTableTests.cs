using Paradise.Authoring;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// Spec item 11 — inline tables — and the asset reference that is the reason it exists.
///
/// The rule these pin hardest is that the FORM is chosen by TYPE, never by inspecting the data.
/// A writer that decided "all-scalar tables go inline" would agree with the Python mirror until
/// the first document where the two read that rule differently, and the disagreement would arrive
/// as a scene-check byte failure with nothing pointing at formatting.
/// </summary>
public class CanonicalInlineTableTests
{
    [Test]
    public async Task an_inline_table_is_written_on_one_line()
    {
        var document = new CanonicalTomlTable
        {
            { "Mesh", new CanonicalInlineTable { { "guid", "5f2a" }, { "path", "Models/x.glb" } } },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document))
            .IsEqualTo("Mesh = { guid = \"5f2a\", path = \"Models/x.glb\" }\n");
    }

    [Test]
    public async Task an_empty_inline_table_is_two_braces()
    {
        // This is the null slot: an array position that carries no reference but must still
        // occupy its place, because slot order is the contract.
        var document = new CanonicalTomlTable { { "Slot", new CanonicalInlineTable() } };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo("Slot = {}\n");
    }

    /// <summary>One widening for every path (issue #200): 0.1f is <c>0.1</c> here as in a table, never the bit-exact 17 digits.</summary>
    [Test]
    public async Task a_float32_widens_to_its_shortest_decimal()
    {
        var document = new CanonicalTomlTable
        {
            { "Slot", new CanonicalInlineTable { { "weight", 0.1f }, { "spread", new object[] { 0.1f, 0.3f } } } },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document))
            .IsEqualTo("Slot = { weight = 0.1, spread = [0.1, 0.3] }\n");
    }

    /// <summary>The reader refuses a one-half reference, so the writer must not produce one (issue #210).</summary>
    [Test]
    public async Task a_reference_with_one_half_missing_cannot_be_written()
    {
        await Assert.That(() => AssetReferenceCodec.Write(new AssetReference(Guid.Empty, "Models/x.glb")))
            .Throws<ArgumentException>();
        await Assert.That(() => AssetReferenceCodec.Write(new AssetReference(Guid.NewGuid(), "")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task inline_tables_nest_inside_arrays()
    {
        var document = new CanonicalTomlTable
        {
            {
                "Slots", new object[]
                {
                    new CanonicalInlineTable { { "guid", "a" }, { "path", "materials/one.toml" } },
                    new CanonicalInlineTable(),
                    new CanonicalInlineTable { { "guid", "b" }, { "path", "materials/two.toml" } },
                }
            },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "Slots = [{ guid = \"a\", path = \"materials/one.toml\" }, {}, "
            + "{ guid = \"b\", path = \"materials/two.toml\" }]\n");
    }

    [Test]
    public async Task a_generic_table_is_still_a_header_even_when_all_its_values_are_scalars()
    {
        // THE property: the form follows the type, not the contents. If this ever emits
        // `t = { a = 1 }` the rule has become data-dependent and the two writers will drift.
        var document = new CanonicalTomlTable { { "t", new CanonicalTomlTable { { "a", 1L } } } };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo("[t]\na = 1\n");
    }

    [Test]
    public async Task keys_and_values_inside_an_inline_table_follow_the_ordinary_rules()
    {
        var document = new CanonicalTomlTable
        {
            {
                "r", new CanonicalInlineTable
                {
                    { "a b", 1.5 },          // needs quoting, item 4
                    { "n", -0.0 },           // negative zero keeps its sign, item 7
                    { "s", "say \"hi\"" },   // escapes, item 5
                    { "list", new object[] { 1L, 2L } },
                }
            },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "r = { \"a b\" = 1.5, n = -0.0, s = \"say \\\"hi\\\"\", list = [1, 2] }\n");
    }

    [Test]
    public async Task model_order_is_preserved_inside_an_inline_table()
    {
        var document = new CanonicalTomlTable
        {
            { "r", new CanonicalInlineTable { { "z", 1L }, { "a", 2L } } },
        };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo("r = { z = 1, a = 2 }\n");
    }

    [Test]
    public async Task a_nested_table_inside_an_inline_table_is_refused()
    {
        var inline = new CanonicalInlineTable();

        await Assert.That(() => inline.Add("nested", new CanonicalInlineTable()))
            .Throws<ArgumentException>();
        await Assert.That(() => inline.Add("nested", new CanonicalTomlTable()))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task a_generic_table_inside_an_array_is_still_refused_and_names_the_fix()
    {
        // The message has to point at CanonicalInlineTable, because "table in an array" is now a
        // legal thing to want and the author needs to know which type expresses it.
        var table = new CanonicalTomlTable();

        var error = Assert.Throws<ArgumentException>(
            () => table.Add("a", new object[] { new CanonicalTomlTable() }));

        await Assert.That(error!.Message).Contains("CanonicalInlineTable");
    }

    [Test]
    public async Task an_inline_table_round_trips_through_the_reader()
    {
        // Read -> write must be the identity, which is what makes scene-check's byte comparison
        // meaningful for documents carrying references.
        const string text = "Slots = [{ guid = \"a\", path = \"materials/one.toml\" }, {}]\n";

        var parsed = TomlDocumentReader.Parse(text, Fail);
        var model = TomlDocumentReader.ToCanonical(parsed, "in the document", Fail);

        await Assert.That(CanonicalTomlWriter.WriteString(model)).IsEqualTo(text);
    }

    [Test]
    public async Task a_reference_under_a_plain_key_round_trips_inline()
    {
        // The case an array-only rule would break, and the shape the format uses most: a single
        // reference is not inside an array, so position cannot say it was written inline.
        const string text = "Mesh = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"Models/x.glb\" }\n";

        var model = TomlDocumentReader.ToCanonical(TomlDocumentReader.Parse(text, Fail), "in the document", Fail);

        await Assert.That(CanonicalTomlWriter.WriteString(model)).IsEqualTo(text);
    }

    [Test]
    public async Task a_table_written_as_a_header_round_trips_as_a_header()
    {
        // The other half of the round trip: an ordinary sub-table must NOT come back inline.
        const string text = "[t]\na = 1\n";

        var model = TomlDocumentReader.ToCanonical(TomlDocumentReader.Parse(text, Fail), "in the document", Fail);

        await Assert.That(CanonicalTomlWriter.WriteString(model)).IsEqualTo(text);
    }

    [Test]
    public async Task an_asset_reference_writes_guid_first_then_path()
    {
        // Order is fixed rather than incidental: the writer emits model order, so the Python
        // mirror has to build the table the same way round to produce the same bytes.
        var reference = new AssetReference(Guid.Parse("11111111-2222-4333-8444-555555555555"), "Models/x.glb");

        var document = new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(reference) } };

        await Assert.That(CanonicalTomlWriter.WriteString(document)).IsEqualTo(
            "Mesh = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"Models/x.glb\" }\n");
    }

    [Test]
    public async Task a_null_asset_reference_writes_as_the_empty_table()
    {
        await Assert.That(CanonicalTomlWriter.WriteString(
            new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(null) } }))
            .IsEqualTo("Mesh = {}\n");
    }

    [Test]
    public async Task reading_an_asset_reference_recovers_both_halves()
    {
        var written = AssetReferenceCodec.Write(
            new AssetReference(Guid.Parse("11111111-2222-4333-8444-555555555555"), "Models/x.glb"));

        var read = AssetReferenceCodec.Read(written, "on a test", Fail);

        await Assert.That(read!.Guid).IsEqualTo(Guid.Parse("11111111-2222-4333-8444-555555555555"));
        await Assert.That(read.Path).IsEqualTo("Models/x.glb");
    }

    [Test]
    public async Task an_empty_asset_reference_reads_as_null()
    {
        await Assert.That(AssetReferenceCodec.Read(new CanonicalInlineTable(), "on a test", Fail)).IsNull();
    }

    [Test]
    public async Task a_path_only_reference_is_refused()
    {
        // Accepting it would resolve today and break on the first rename -- the exact failure the
        // guid is carried to prevent, so half a reference is worse than none.
        var partial = new CanonicalInlineTable { { "path", "Models/x.glb" } };

        await Assert.That(() => AssetReferenceCodec.Read(partial, "on a test", Fail))
            .Throws<FormatException>();
    }

    [Test]
    public async Task a_malformed_guid_is_refused()
    {
        var bad = new CanonicalInlineTable { { "guid", "not-a-uuid" }, { "path", "Models/x.glb" } };

        await Assert.That(() => AssetReferenceCodec.Read(bad, "on a test", Fail))
            .Throws<FormatException>();
    }

    [Test]
    public async Task a_bare_string_is_not_an_asset_reference()
    {
        await Assert.That(() => AssetReferenceCodec.Read("Models/x.glb", "on a test", Fail))
            .Throws<FormatException>();
    }

    [Test]
    public async Task try_read_takes_a_well_formed_reference()
    {
        var table = new CanonicalInlineTable
        {
            { "guid", "5f2a6666-7777-4888-8999-aaaaaaaaaaaa" },
            { "path", "models/x.glb" },
        };

        await Assert.That(AssetReferenceCodec.TryRead(table, out var reference)).IsTrue();
        await Assert.That(reference.Guid).IsEqualTo(Guid.Parse("5f2a6666-7777-4888-8999-aaaaaaaaaaaa"));
        await Assert.That(reference.Path).IsEqualTo("models/x.glb");
    }

    /// <summary>A rewrite walks every table in a document, so the malformed ones must come back as "not a reference" rather than as an exception that stops the walk.</summary>
    [Test]
    [Arguments("not-a-uuid", "models/x.glb")]
    [Arguments("00000000-0000-0000-0000-000000000000", "models/x.glb")]
    [Arguments("5f2a6666-7777-4888-8999-aaaaaaaaaaaa", "")]
    public async Task try_read_refuses_without_throwing(string guid, string path)
    {
        var table = new CanonicalInlineTable { { "guid", guid }, { "path", path } };

        await Assert.That(AssetReferenceCodec.TryRead(table, out _)).IsFalse();
    }

    [Test]
    public async Task try_read_refuses_the_empty_table()
    {
        await Assert.That(AssetReferenceCodec.TryRead(new CanonicalInlineTable(), out _)).IsFalse();
    }

    private static Exception Fail(string problem) => new FormatException(problem);
}

using Tomlyn.Model;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The duplicate-key walk against the reader directly, on documents no prefab would carry: what
/// TOML 1.0 and tomllib refuse is refused here, and what they accept is not (issues #198, #219).
/// </summary>
public class TomlDuplicateKeysTests
{
    private static Exception Fail(string problem) => new FormatException(problem);

    private static TomlTable Parse(string text) => TomlDocumentReader.Parse(text, Fail);

    private static FormatException Rejects(string text) => Assert.Throws<FormatException>(() => Parse(text))!;

    [Test]
    public async Task a_subtable_in_one_array_element_and_a_plain_key_in_the_next_are_distinct()
    {
        // The #219 shape: Tomlyn's own validator reports element 1's `Value` as a redefinition
        // of element 0's [components.Value].
        var table = Parse("[[components]]\ntype = \"a\"\n[components.Value]\nr = 1\n[[components]]\ntype = \"b\"\nValue = 1\n");

        await Assert.That(((TomlTableArray)table["components"]).Count).IsEqualTo(2);
    }

    [Test]
    public async Task a_quoted_dotted_key_is_one_key_and_does_not_alias_the_nested_path()
    {
        // "a.b" is a single key spelled a.b; [a] b = 2 is a different path. A walk keyed by a
        // joined string would call the second a redefinition of the first.
        var table = Parse("\"a.b\" = 1\n[a]\nb = 2\n");

        await Assert.That(table.Keys).Contains("a.b");
        await Assert.That(((TomlTable)table["a"])["b"]).IsEqualTo(2L);
    }

    [Test]
    public async Task a_quoted_key_spelled_like_an_array_element_does_not_alias_one()
    {
        var table = Parse("[[a]]\nx = 1\n[\"a[0]\"]\nx = 2\n");

        await Assert.That(table.Keys).Contains("a[0]");
    }

    [Test]
    public async Task a_table_created_by_a_dotted_key_cannot_be_reopened_by_a_header()
    {
        // TOML 1.0: a dotted key defines its tables, and a later [header] for one of them is a
        // redefinition. tomllib refuses it.
        var error = Rejects("[fruit]\napple.color = \"red\"\n[fruit.apple]\ntexture = \"smooth\"\n");

        await Assert.That(error.Message).Contains("fruit.apple");
        await Assert.That(error.Message).Contains("line 3");
    }

    [Test]
    public async Task a_header_may_define_a_table_an_earlier_deeper_header_implied()
    {
        var table = Parse("[a.deep]\nx = 1\n[a]\ny = 2\n");

        await Assert.That(((TomlTable)table["a"])["y"]).IsEqualTo(2L);
    }

    [Test]
    public async Task a_value_cannot_be_reopened_as_a_table_and_the_message_names_the_line()
    {
        var error = Rejects("a = 1\n[a.b]\nx = 1\n");

        await Assert.That(error.Message).Contains("already defined as a value");
        await Assert.That(error.Message).Contains("line 2");
    }

    [Test]
    public async Task a_duplicate_inside_an_inline_table_is_refused()
    {
        var error = Rejects("point = { x = 1, x = 2 }\n");

        await Assert.That(error.Message).Contains("point.x");
    }

    [Test]
    public async Task inline_tables_in_one_array_are_separate_scopes()
    {
        var table = Parse("points = [ { x = 1 }, { x = 2 } ]\n");

        await Assert.That(((TomlArray)table["points"]).Count).IsEqualTo(2);
    }

    [Test]
    public async Task a_duplicate_key_at_the_root_is_refused()
    {
        var error = Rejects("a = 1\na = 2\n");

        await Assert.That(error.Message).Contains("`a`");
        await Assert.That(error.Message).Contains("line 2");
    }
}

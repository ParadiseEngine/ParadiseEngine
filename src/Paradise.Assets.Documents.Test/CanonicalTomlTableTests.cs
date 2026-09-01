namespace Paradise.Assets.Documents.Test;

public class CanonicalTomlTableTests
{
    [Test]
    public async Task keys_cannot_repeat()
    {
        var table = new CanonicalTomlTable { { "a", 1 } };

        await Assert.That(() => table.Add("a", 2)).Throws<ArgumentException>();
        await Assert.That(table.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ints_and_floats_widen_to_the_canonical_types()
    {
        var table = new CanonicalTomlTable { { "i", 3 }, { "f", 1.5f } };

        var values = table.ToDictionary(pair => pair.Key, pair => pair.Value);
        await Assert.That(values["i"]).IsEqualTo(3L);
        await Assert.That(values["f"]).IsEqualTo(1.5d);
    }

    [Test]
    public async Task values_outside_the_vocabulary_are_rejected()
    {
        var table = new CanonicalTomlTable();

        // Dates deliberately excluded: no authored document needs one, and TOML datetimes are
        // where implementations disagree most.
        await Assert.That(() => table.Add("when", DateTime.UtcNow)).Throws<ArgumentException>();
        await Assert.That(() => table.Add("what", new object())).Throws<ArgumentException>();
    }

    [Test]
    public async Task a_table_inside_a_plain_array_is_rejected()
    {
        var table = new CanonicalTomlTable();

        await Assert.That(() => table.Add("bad", new object[] { new CanonicalTomlTable() }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task array_contents_are_copied_and_widened()
    {
        var source = new List<object> { 1, 2 };
        var table = new CanonicalTomlTable { { "a", source } };
        source.Add(3);

        await Assert.That(CanonicalTomlWriter.WriteString(table)).IsEqualTo("a = [1, 2]\n");
    }
}

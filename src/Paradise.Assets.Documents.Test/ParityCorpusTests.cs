using System.Text;

namespace Paradise.Assets.Documents.Test;

/// <summary>
/// The cross-language byte contract, pinned by files rather than by vectors typed twice: each
/// fixture was written by this writer, and reading it back and writing it again must reproduce
/// the bytes. The Blender addon runs the same test over the same files (issue #209).
/// </summary>
public class ParityCorpusTests
{
    private static readonly string s_directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity");
    private static readonly UTF8Encoding s_utf8 = new(false);

    public static IEnumerable<Func<string>> TomlFixtures()
        => Directory.EnumerateFiles(s_directory, "*.toml").Order().Select(path => (Func<string>)(() => path));

    [Test]
    [MethodDataSource(nameof(TomlFixtures))]
    public async Task a_canonical_toml_fixture_is_a_fixed_point_of_read_then_write(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = s_utf8.GetString(bytes);

        var table = TomlDocumentReader.Parse(text, static problem => new FormatException(problem));
        var model = TomlDocumentReader.ToCanonical(table, "in the fixture", static problem => new FormatException(problem));

        await Assert.That(CanonicalTomlWriter.WriteBytes(model)).IsEquivalentTo(bytes).Because(Path.GetFileName(path));
    }

    [Test]
    public async Task the_prefab_fixture_is_a_fixed_point_of_read_then_write()
    {
        var path = Path.Combine(s_directory, "prefab.prefab");
        var text = s_utf8.GetString(File.ReadAllBytes(path));

        var document = PrefabDocumentSerializer.Parse(text, "prefab.prefab");

        await Assert.That(PrefabDocumentSerializer.Write(document)).IsEqualTo(text);
    }

    [Test]
    public async Task the_corpus_is_present()
    {
        await Assert.That(Directory.EnumerateFiles(s_directory, "*.toml").Count()).IsEqualTo(3);
    }
}

namespace Paradise.Assets.Documents.Test;

/// <summary>The game's own TOML: canonical rewrite for the check verb, JSON for a json-profile build.</summary>
public class ConfigDocumentTests
{
    [Test]
    public async Task canonicalize_normalises_spelling_and_keeps_order()
    {
        var ok = ConfigDocument.TryCanonicalize("b=2\na = 1.50\n[t]\nx = \"y\"\n", out var canonical, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsEqualTo("b = 2\na = 1.5\n\n[t]\nx = \"y\"\n");
    }

    [Test]
    public async Task a_duplicate_key_is_refused_rather_than_last_wins()
    {
        var ok = ConfigDocument.TryCanonicalize("a = 1\na = 2\n", out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("a");
    }

    [Test]
    public async Task json_keeps_structure_and_references_as_objects()
    {
        var json = ConfigDocument.ToJson(
            "name = \"x\"\nflags = [true, false]\nMesh = { guid = \"11111111-2222-4333-8444-555555555555\", path = \"Models/x.glb\" }\n\n[tuning]\nspeed = 2\n",
            "config.toml");

        await Assert.That(json).Contains("\"name\": \"x\"");
        await Assert.That(json).Contains("\"flags\": [");
        await Assert.That(json).Contains("\"path\": \"Models/x.glb\"");
        await Assert.That(json).Contains("\"speed\": 2");
    }

    /// <summary>JSON has one number type: an integral float lands as an integer, which every typed reader accepts. Pinned so nobody expects byte fidelity.</summary>
    [Test]
    public async Task an_integral_float_becomes_an_integer_in_json()
    {
        var json = ConfigDocument.ToJson("scale = 1.0\nzero = -0.0\n", "config.toml");

        await Assert.That(json).Contains("\"scale\": 1");
        await Assert.That(json).DoesNotContain("1.0");
    }

    /// <summary>JSON has no spelling for inf or nan; emitting a string a reader would take for text is worse than refusing (issue #211).</summary>
    [Test]
    public async Task inf_and_nan_are_refused_with_the_source_and_key_named()
    {
        var inf = Assert.Throws<FormatException>(() => ConfigDocument.ToJson("[limits]\nmax = inf\n", "config.toml"));
        await Assert.That(inf.Message).Contains("config.toml");
        await Assert.That(inf.Message).Contains("limits.max");
        await Assert.That(inf.Message).Contains("inf");

        var nan = Assert.Throws<FormatException>(() => ConfigDocument.ToJson("v = [1.0, nan]\n", "config.toml"));
        await Assert.That(nan.Message).Contains("v[1]");
    }
}

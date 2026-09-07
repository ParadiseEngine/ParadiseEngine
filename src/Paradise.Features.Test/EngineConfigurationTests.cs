using System.Text;

namespace Paradise.Features.Test;

/// <summary>The file, the flag and the environment variable — the three layers a person
/// configures a build through, and the order they win in.</summary>
public class EngineConfigurationTests
{
    [Test]
    public async Task a_document_reads_its_features()
    {
        var config = EngineConfiguration.Read("""
            {
              "features": {
                "rendering.bloom": false,
                "rendering.globalIllumination": true
              }
            }
            """);

        await Assert.That(config.Features.TryGet("rendering.bloom", out var bloom) && !bloom).IsTrue();
        await Assert.That(config.Features.TryGet("rendering.globalIllumination", out var gi) && gi).IsTrue();
    }

    /// <summary>Hand-edited by design, the same latitude the engine's other hand-edited documents
    /// get: a comment saying WHY a feature is off is the most useful line in such a file.</summary>
    [Test]
    public async Task comments_and_a_trailing_comma_are_allowed()
    {
        var config = EngineConfiguration.Read("""
            {
              // The integrated GPU cannot afford the probe trace.
              "features": {
                "rendering.globalIllumination": false,
              },
            }
            """);

        await Assert.That(config.Features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_stream_reads_the_same_document()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{"features":{"rendering.bloom":false}}"""));

        var config = EngineConfiguration.Read(stream);

        await Assert.That(config.Features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_document_with_no_features_section_configures_nothing()
    {
        var config = EngineConfiguration.Read("""{"somethingElse": 1}""");

        await Assert.That(config.Features.Count).IsEqualTo(0);
    }

    /// <summary>Two spellings of one name is how a config file starts disagreeing with itself, so
    /// the nested shape is refused with the flat one in the message rather than guessed at.</summary>
    [Test]
    public async Task a_nested_features_object_is_refused_by_name()
    {
        await Assert.That(() => EngineConfiguration.Read("""{"features":{"rendering":{"bloom":false}}}"""))
            .Throws<FormatException>().WithMessageContaining("rendering.<feature>");
    }

    [Test]
    public async Task a_value_that_is_not_a_boolean_is_refused()
    {
        await Assert.That(() => EngineConfiguration.Read("""{"features":{"rendering.bloom":"off"}}"""))
            .Throws<FormatException>().WithMessageContaining("rendering.bloom");
    }

    [Test]
    public async Task malformed_json_names_itself_as_a_configuration_problem()
    {
        await Assert.That(() => EngineConfiguration.Read("{ nope"))
            .Throws<FormatException>().WithMessageContaining("not valid JSON");
    }

    /// <summary>Nearest to the person running the build wins.</summary>
    [Test]
    public async Task a_later_layer_wins_name_by_name()
    {
        var file = EngineConfiguration.Read("""{"features":{"rendering.bloom":false,"rendering.shadows":false}}""");
        var command = new EngineConfiguration { Features = FeatureOverrides.Parse("+rendering.bloom") };

        var merged = file.Merge(command);

        await Assert.That(merged.Features.TryGet("rendering.bloom", out var bloom) && bloom).IsTrue();
        await Assert.That(merged.Features.TryGet("rendering.shadows", out var shadows) && !shadows).IsTrue();
    }
}

/// <summary>The syntax of a <c>--features</c> flag and of <c>PARADISE_FEATURES</c>.</summary>
public class FeatureOverridesParseTests
{
    [Test]
    public async Task a_bare_name_turns_a_feature_on_and_a_minus_turns_it_off()
    {
        var overrides = FeatureOverrides.Parse("rendering.bloom,-rendering.shadows,+rendering.gi,!rendering.ssr");

        await Assert.That(overrides.TryGet("rendering.bloom", out var bloom) && bloom).IsTrue();
        await Assert.That(overrides.TryGet("rendering.shadows", out var shadows) && !shadows).IsTrue();
        await Assert.That(overrides.TryGet("rendering.gi", out var gi) && gi).IsTrue();
        await Assert.That(overrides.TryGet("rendering.ssr", out var ssr) && !ssr).IsTrue();
    }

    /// <summary>A shell that splits the argument and one that does not both work.</summary>
    [Test]
    public async Task commas_semicolons_and_spaces_all_separate()
    {
        var overrides = FeatureOverrides.Parse(" a.one; -a.two   a.three ");

        await Assert.That(overrides.Count).IsEqualTo(3);
    }

    [Test]
    public async Task an_explicit_value_is_accepted_in_the_spellings_people_type()
    {
        var overrides = FeatureOverrides.Parse("a.one=false,a.two=off,a.three=0,a.four=yes");

        await Assert.That(overrides.TryGet("a.one", out var one) && !one).IsTrue();
        await Assert.That(overrides.TryGet("a.two", out var two) && !two).IsTrue();
        await Assert.That(overrides.TryGet("a.three", out var three) && !three).IsTrue();
        await Assert.That(overrides.TryGet("a.four", out var four) && four).IsTrue();
    }

    [Test]
    public async Task a_value_that_is_not_a_boolean_is_refused()
    {
        await Assert.That(() => FeatureOverrides.Parse("a.one=maybe"))
            .Throws<FormatException>().WithMessageContaining("a.one");
    }

    [Test]
    public async Task an_empty_list_is_the_shared_empty_layer()
    {
        await Assert.That(FeatureOverrides.Parse(null)).IsSameReferenceAs(FeatureOverrides.None);
        await Assert.That(FeatureOverrides.Parse("   ")).IsSameReferenceAs(FeatureOverrides.None);
    }
}

/// <summary>A name that reaches three places that never see each other; validated once, here.</summary>
public class FeatureIdTests
{
    [Test]
    public async Task a_dotted_name_parses_and_keeps_its_spelling()
    {
        var id = new FeatureId("rendering.globalIllumination");

        await Assert.That(id.Value).IsEqualTo("rendering.globalIllumination");
        await Assert.That(id.Subsystem.ToString()).IsEqualTo("rendering");
        await Assert.That(id.ToString()).IsEqualTo("rendering.globalIllumination");
    }

    [Test]
    [Arguments("")]
    [Arguments("rendering.")]
    [Arguments(".bloom")]
    [Arguments("rendering..bloom")]
    [Arguments("rendering bloom")]
    [Arguments("rendering/bloom")]
    public async Task a_malformed_name_is_refused_by_both_ways_in(string name)
    {
        await Assert.That(() => new FeatureId(name)).Throws<ArgumentException>();
        await Assert.That(FeatureId.TryParse(name, out _)).IsFalse();
    }

    [Test]
    public async Task two_spellings_of_one_name_are_the_same_id()
    {
        await Assert.That(new FeatureId("Rendering.Bloom")).IsEqualTo(new FeatureId("rendering.bloom"));
        await Assert.That(new FeatureId("Rendering.Bloom").GetHashCode())
            .IsEqualTo(new FeatureId("rendering.bloom").GetHashCode());
    }

    [Test]
    public async Task the_default_id_names_nothing()
    {
        await Assert.That(default(FeatureId).IsEmpty).IsTrue();
        await Assert.That(default(FeatureId).Value).IsEqualTo("");
        await Assert.That(() => new FeatureDefinition(default(FeatureId), true)).Throws<ArgumentException>();
    }
}

using System.Text;

namespace Paradise.Features.Test;

/// <summary>The file, the flag and the environment variable — the three layers a person
/// configures a build through, and the order they win in.</summary>
public class EngineConfigurationTests
{
    [Test]
    public async Task a_document_reads_its_features()
    {
        var config = TomlEngineConfiguration.Read("""
            [[features]]
            name = "rendering.bloom"
            enabled = false

            [[features]]
            name = "rendering.globalIllumination"
            enabled = true
            """);

        await Assert.That(config.Features.TryGet("rendering.bloom", out var bloom) && !bloom).IsTrue();
        await Assert.That(config.Features.TryGet("rendering.globalIllumination", out var gi) && gi).IsTrue();
    }

    /// <summary>Hand-edited by design, which is most of why the file is TOML: a comment saying WHY
    /// a feature is off is the most useful line in such a file.</summary>
    [Test]
    public async Task comments_are_allowed()
    {
        var config = TomlEngineConfiguration.Read("""
            # The integrated GPU cannot afford the probe trace.
            [[features]]
            name = "rendering.globalIllumination"
            enabled = false   # measured at 2.3 ms
            """);

        await Assert.That(config.Features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_stream_reads_the_same_document()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "[[features]]\nname = \"rendering.bloom\"\nenabled = false"));

        var config = TomlEngineConfiguration.Read(stream);

        await Assert.That(config.Features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_document_with_no_features_configures_nothing()
    {
        var config = TomlEngineConfiguration.Read("somethingElse = 1");

        await Assert.That(config.Features.Count).IsEqualTo(0);
        await Assert.That(config.Settings.Count).IsEqualTo(0);
    }

    /// <summary>The name is a VALUE, so a dotted name needs no quoting rule and cannot be read as
    /// table nesting. Writing the old table shape says so.</summary>
    [Test]
    public async Task a_features_table_is_refused_with_the_entry_shape()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("""
            [features]
            "rendering.bloom" = false
            """))
            .Throws<FormatException>().WithMessageContaining("[[features]]");
    }

    [Test]
    public async Task an_entry_with_no_name_is_refused()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("""
            [[features]]
            enabled = false
            """))
            .Throws<FormatException>().WithMessageContaining("name");
    }

    [Test]
    public async Task an_enabled_that_is_a_quoted_boolean_says_to_unquote_it()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("""
            [[features]]
            name = "rendering.bloom"
            enabled = "true"
            """))
            .Throws<FormatException>().WithMessageContaining("enabled = true");
    }

    [Test]
    public async Task an_enabled_that_is_not_a_boolean_at_all_is_refused()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("""
            [[features]]
            name = "rendering.bloom"
            enabled = 1
            """))
            .Throws<FormatException>().WithMessageContaining("rendering.bloom");
    }

    /// <summary>Two entries for one feature is a copy-paste; last-wins would drop the first in
    /// silence, which is the failure this repo has been bitten by in TOML before.</summary>
    [Test]
    public async Task one_feature_may_not_have_two_entries()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("""
            [[features]]
            name = "rendering.bloom"
            enabled = false

            [[features]]
            name = "rendering.bloom"
            enabled = true
            """))
            .Throws<FormatException>().WithMessageContaining("more than one");
    }

    /// <summary>An entry may configure a feature without saying whether it runs — a feature that
    /// ships on needs only its settings.</summary>
    [Test]
    public async Task an_entry_may_carry_settings_and_no_switch()
    {
        var config = TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = 0.6
            """);

        await Assert.That(config.Features.Count).IsEqualTo(0);
        await Assert.That(config.Settings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task malformed_toml_names_itself_as_a_configuration_problem()
    {
        await Assert.That(() => TomlEngineConfiguration.Read("[[features"))
            .Throws<FormatException>().WithMessageContaining("not valid TOML");
    }

    /// <summary>The layer a host starts from before it has read anything, and the one every test
    /// above happens to skip because the reader fills both sections in. It has to be usable: its
    /// sections are empty, not null. They were null once — a static initializer above the field it
    /// reads — and nothing noticed until a real host merged onto it and iterated the result.</summary>
    [Test]
    public async Task the_empty_layer_is_usable_as_a_starting_point()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Empty);
        var merged = EngineConfiguration.Empty
            .Merge(new EngineConfiguration { Features = FeatureOverrides.Parse("-rendering.bloom") });
        var mergedSwitches = new FeatureSwitches(merged);

        await Assert.That(EngineConfiguration.Empty.Features.Count).IsEqualTo(0);
        await Assert.That(EngineConfiguration.Empty.Settings.Count).IsEqualTo(0);
        await Assert.That(switches.Definitions).IsEmpty();
        await Assert.That(mergedSwitches.IsEnabled(new FeatureId("rendering.bloom"))).IsFalse();
    }

    /// <summary>Nearest to the person running the build wins.</summary>
    [Test]
    public async Task a_later_layer_wins_name_by_name()
    {
        var file = TomlEngineConfiguration.Read("""
            [[features]]
            name = "rendering.bloom"
            enabled = false

            [[features]]
            name = "rendering.shadows"
            enabled = false
            """);
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

    /// <summary>A record's own equality would compare the wrapped string ordinally, so this checks
    /// every door into it goes through the case-insensitive one the type promises — the
    /// synthesized <c>==</c> included, which is the one the record would quietly take over.</summary>
    [Test]
    public async Task two_spellings_of_one_name_are_the_same_id()
    {
        var upper = new FeatureId("Rendering.Bloom");
        var lower = new FeatureId("rendering.bloom");

        await Assert.That(upper).IsEqualTo(lower);
        await Assert.That(upper == lower).IsTrue();
        await Assert.That(upper != lower).IsFalse();
        await Assert.That(upper.Equals((object)lower)).IsTrue();
        await Assert.That(upper.GetHashCode()).IsEqualTo(lower.GetHashCode());
        await Assert.That(upper.ToString()).IsEqualTo("Rendering.Bloom"); // the spelling survives
    }

    [Test]
    public async Task the_default_id_names_nothing()
    {
        await Assert.That(default(FeatureId).IsEmpty).IsTrue();
        await Assert.That(default(FeatureId).Value).IsEqualTo("");
        await Assert.That(() => new FeatureDefinition(default(FeatureId), true)).Throws<ArgumentException>();
    }
}

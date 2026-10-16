using System.Text;

namespace Paradise.Features.Test;

/// <summary>Checks TOML configuration parsing and later-layer precedence.</summary>
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

        await Assert.That(config.Features.TryGet("rendering.bloom", out var bloom)).IsTrue();
        await Assert.That(bloom).IsFalse();
        await Assert.That(config.Features.TryGet("rendering.globalIllumination", out var gi)).IsTrue();
        await Assert.That(gi).IsTrue();
    }

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
        await Assert.That(config.Features.TryGet("rendering.globalIllumination", out var enabled)).IsTrue();
        await Assert.That(enabled).IsFalse();
    }

    [Test]
    public async Task a_stream_reads_the_feature_state()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "[[features]]\nname = \"rendering.bloom\"\nenabled = false"));

        var config = TomlEngineConfiguration.Read(stream);

        await Assert.That(config.Features.Count).IsEqualTo(1);
        await Assert.That(config.Features.TryGet("rendering.bloom", out var enabled)).IsTrue();
        await Assert.That(enabled).IsFalse();
    }

    [Test]
    public async Task a_document_with_no_features_configures_nothing()
    {
        var config = TomlEngineConfiguration.Read("somethingElse = 1");

        await Assert.That(config.Features.Count).IsEqualTo(0);
        await Assert.That(config.Settings.Count).IsEqualTo(0);
    }

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

    // Reject duplicate entries instead of silently losing the first entry's settings.
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

        await Assert.That(merged.Features.TryGet("rendering.bloom", out var bloom)).IsTrue();
        await Assert.That(bloom).IsTrue();
        await Assert.That(merged.Features.TryGet("rendering.shadows", out var shadows)).IsTrue();
        await Assert.That(shadows).IsFalse();
    }
}

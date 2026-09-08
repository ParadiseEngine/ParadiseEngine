using TUnit.Assertions.Enums;
using System.Text.Json.Serialization;
using Tomlyn.Serialization;

namespace Paradise.Features.Test;

/// <summary>Models game settings with source-generated, camelCase TOML binding.</summary>
/// <remarks>Use get/set properties to preserve defaults for omitted keys; tests cover both requirements.</remarks>
public sealed record WeatherSettings
{
    public float Intensity { get; set; } = 1f;
    public float WindMetresPerSecond { get; set; } = 2f;
    public string Kind { get; set; } = "rain";
}

[TomlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[TomlSerializable(typeof(WeatherSettings))]
internal sealed partial class GameToml : TomlSerializerContext;

/// <summary>The other half of configuring a feature: not only whether it runs, but what it runs
/// with — for a feature the engine has never heard of.</summary>
public class FeatureSettingsTests
{
    private static readonly FeatureId s_weather = new("game.weather");

    private const string Document = """
        [[features]]
        name = "game.weather"
        enabled = true
        intensity = 0.6
        windMetresPerSecond = 3.5
        """;

    [Test]
    public async Task a_game_feature_reads_what_the_file_configured_it_with()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read(Document));
        switches.Declare(new FeatureDefinition("game.weather", true, "Rain and wind."));

        var settings = switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default);

        await Assert.That(switches.IsEnabled(s_weather)).IsTrue();
        await Assert.That(settings.Intensity).IsEqualTo(0.6f);
        await Assert.That(settings.WindMetresPerSecond).IsEqualTo(3.5f);
        // Not written: the record's own default, not a zero.
        await Assert.That(settings.Kind).IsEqualTo("rain");
        await Assert.That(switches.Unknown).IsEmpty();
    }

    /// <summary>Verifies get/set settings retain initializers for omitted keys.</summary>
    [Test]
    public async Task an_unwritten_property_keeps_its_initializer()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            kind = "snow"
            """));

        var settings = switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default);

        await Assert.That(settings.Kind).IsEqualTo("snow");
        await Assert.That(settings.Intensity).IsEqualTo(1f);
        await Assert.That(settings.WindMetresPerSecond).IsEqualTo(2f);
    }

    /// <summary>No branch at the call site: a feature nobody configured reads its own
    /// defaults.</summary>
    [Test]
    public async Task a_feature_nobody_configured_reads_its_defaults()
    {
        var switches = new FeatureSwitches();

        var settings = switches.SettingsFor(s_weather);

        await Assert.That(settings.IsEmpty).IsTrue();
        await Assert.That(settings).IsSameReferenceAs(FeatureSettings.None);
        await Assert.That(settings.Read<WeatherSettings>(GameToml.Default)).IsEqualTo(new WeatherSettings());
    }

    /// <summary>Settings and the switch are written independently: a feature that ships on only
    /// ever needs the settings half.</summary>
    [Test]
    public async Task settings_without_a_switch_are_kept()
    {
        var config = TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = 0.25
            """);

        var switches = new FeatureSwitches(config);
        switches.Declare(new FeatureDefinition("game.weather", true));

        await Assert.That(switches.IsEnabled(s_weather)).IsTrue();
        await Assert.That(switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default).Intensity)
            .IsEqualTo(0.25f);
    }

    /// <summary>The same rule the switches follow: the file is read before the game's subsystems
    /// exist, so settings land on a name nothing has declared yet and are still there when it
    /// arrives.</summary>
    [Test]
    public async Task settings_survive_arriving_before_the_declaration()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read(Document));

        var beforeDeclaring = switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default);
        switches.Declare(new FeatureDefinition("game.weather", true));

        await Assert.That(beforeDeclaring.Intensity).IsEqualTo(0.6f);
        await Assert.That(switches.Unknown).IsEmpty();
    }

    /// <summary>A settings block naming no feature is as stale as an override naming none.</summary>
    [Test]
    public async Task settings_for_a_name_nobody_declares_are_reported()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.gone"
            intensity = 1.0
            """));

        // Unordered deliberately: Unknown concatenates the keys of two ConcurrentDictionary
        // instances, whose enumeration order is not part of their contract.
        await Assert.That(switches.Unknown).IsEquivalentTo(["game.gone"]);
    }

    /// <summary>Re-reading <c>engine.json</c> is a live change: a feature that read its settings
    /// once hears that they moved.</summary>
    [Test]
    public async Task replacing_settings_announces_the_new_ones()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read(Document));
        switches.Declare(new FeatureDefinition("game.weather", true));
        var announced = new List<float>();
        switches.SettingsChanged += (_, settings) =>
            announced.Add(settings.Read<WeatherSettings>(GameToml.Default).Intensity);

        switches.Apply(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = 0.9
            """));
        // The same text again: nothing moved, nothing announced.
        switches.Apply(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = 0.9
            """));

        await Assert.That(announced).IsEquivalentTo([0.9f], CollectionOrdering.Matching);
        await Assert.That(switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default).Intensity)
            .IsEqualTo(0.9f);
    }

    /// <summary>Wholesale, not deep-merged — a later file that means to change one field writes
    /// the object it wants. Anything else has no answer for "which layer owns element 3".</summary>
    [Test]
    public async Task a_later_layer_replaces_a_feature_s_settings_whole()
    {
        var merged = TomlEngineConfiguration.Read(Document)
            .Merge(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            kind = "snow"
            """));
        var switches = new FeatureSwitches(merged);

        var settings = switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default);

        await Assert.That(settings.Kind).IsEqualTo("snow");
        await Assert.That(settings.Intensity).IsEqualTo(1f); // the earlier layer's 0.6 is gone, not merged
    }

    [Test]
    public async Task a_layer_that_says_nothing_about_settings_leaves_them_alone()
    {
        var merged = TomlEngineConfiguration.Read(Document)
            .Merge(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            enabled = false
            """));
        var switches = new FeatureSwitches(merged);

        await Assert.That(switches.IsEnabled(s_weather)).IsFalse();
        await Assert.That(switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default).Intensity)
            .IsEqualTo(0.6f);
    }

    /// <summary>Verifies reader-reserved keys cannot also become feature settings.</summary>
    [Test]
    public async Task the_reserved_keys_are_not_settings()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read(Document));

        var text = switches.SettingsFor(s_weather).Text;

        await Assert.That(text).Contains("intensity");
        await Assert.That(text).DoesNotContain("name");
        await Assert.That(text).DoesNotContain("enabled");
        await Assert.That(TomlEngineConfiguration.ReservedKeys).IsEquivalentTo(["name", "enabled"], CollectionOrdering.Matching);
    }

    /// <summary>A settings object that does not fit the record names the FEATURE, because that is
    /// how the file spells the thing that has to be fixed.</summary>
    [Test]
    public async Task settings_that_do_not_fit_the_type_name_the_feature()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = "a lot"
            """));

        await Assert.That(() => switches.SettingsFor(s_weather).Read<WeatherSettings>(GameToml.Default))
            .Throws<FormatException>().WithMessageContaining("game.weather");
    }

    /// <summary>The escape hatch for a game whose settings are not a record: the TOML the file
    /// said, unconverted.</summary>
    [Test]
    public async Task the_text_is_reachable_as_the_file_wrote_it()
    {
        var switches = new FeatureSwitches(TomlEngineConfiguration.Read("""
            [[features]]
            name = "game.weather"
            intensity = 0.5
            """));

        await Assert.That(switches.SettingsFor(s_weather).Text).Contains("intensity");
        await Assert.That(FeatureSettings.None.Text).IsEmpty();
    }
}

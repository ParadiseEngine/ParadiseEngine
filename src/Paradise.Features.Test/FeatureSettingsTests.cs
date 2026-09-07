using System.Text.Json.Serialization;

namespace Paradise.Features.Test;

/// <summary>A game's own settings record, and the source-generated metadata that binds it. Three
/// lines in a game, and the reason nothing in <c>Paradise.Features</c> ever reflects over a type
/// it was not handed: this stays AOT- and trim-clean.
///
/// <para><c>get; set;</c> rather than the <c>init</c> the rest of this repo's data records use —
/// see <see cref="FeatureSettings.Read{T}"/>: an init-only property loses its initializer through
/// System.Text.Json, so a settings object that leaves it out would read as 0 rather than the
/// default. <see cref="FeatureSettingsTests.an_unwritten_property_keeps_its_initializer"/> is the
/// guard.</para></summary>
public sealed record WeatherSettings
{
    public float Intensity { get; set; } = 1f;
    public float WindMetresPerSecond { get; set; } = 2f;
    public string Kind { get; set; } = "rain";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WeatherSettings))]
internal sealed partial class GameJson : JsonSerializerContext;

/// <summary>The other half of configuring a feature: not only whether it runs, but what it runs
/// with — for a feature the engine has never heard of.</summary>
public class FeatureSettingsTests
{
    private static readonly FeatureId s_weather = new("game.weather");

    private const string Document = """
        {
          "features": { "game.weather": true },
          "settings": {
            "game.weather": { "intensity": 0.6, "windMetresPerSecond": 3.5 }
          }
        }
        """;

    [Test]
    public async Task a_game_feature_reads_what_the_file_configured_it_with()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(Document));
        switches.Declare(new FeatureDefinition("game.weather", true, "Rain and wind."));

        var settings = switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings);

        await Assert.That(switches.IsEnabled(s_weather)).IsTrue();
        await Assert.That(settings.Intensity).IsEqualTo(0.6f);
        await Assert.That(settings.WindMetresPerSecond).IsEqualTo(3.5f);
        // Not written: the record's own default, not a zero.
        await Assert.That(settings.Kind).IsEqualTo("rain");
        await Assert.That(switches.Unknown).IsEmpty();
    }

    /// <summary>A settings object that writes some of the properties leaves the rest at the
    /// type's own defaults — which holds ONLY while those properties are <c>get; set;</c>. An
    /// <c>init</c> accessor makes System.Text.Json build the object without running the
    /// initializers, and an unwritten <c>float</c> then reads 0 rather than the default, silently.
    /// Both halves are pinned here because the second is invisible until a scene looks wrong.</summary>
    [Test]
    public async Task an_unwritten_property_keeps_its_initializer()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(
            """{"settings":{"game.weather":{"kind":"snow"}}}"""));

        var settings = switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings);

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
        await Assert.That(settings.Read(GameJson.Default.WeatherSettings)).IsEqualTo(new WeatherSettings());
    }

    /// <summary>Settings and the switch are written independently: a feature that ships on only
    /// ever needs the settings half.</summary>
    [Test]
    public async Task settings_without_a_switch_are_kept()
    {
        var config = EngineConfiguration.Read("""{"settings":{"game.weather":{"intensity":0.25}}}""");

        var switches = new FeatureSwitches(config);
        switches.Declare(new FeatureDefinition("game.weather", true));

        await Assert.That(switches.IsEnabled(s_weather)).IsTrue();
        await Assert.That(switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings).Intensity)
            .IsEqualTo(0.25f);
    }

    /// <summary>The same rule the switches follow: the file is read before the game's subsystems
    /// exist, so settings land on a name nothing has declared yet and are still there when it
    /// arrives.</summary>
    [Test]
    public async Task settings_survive_arriving_before_the_declaration()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(Document));

        var beforeDeclaring = switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings);
        switches.Declare(new FeatureDefinition("game.weather", true));

        await Assert.That(beforeDeclaring.Intensity).IsEqualTo(0.6f);
        await Assert.That(switches.Unknown).IsEmpty();
    }

    /// <summary>A settings block naming no feature is as stale as an override naming none.</summary>
    [Test]
    public async Task settings_for_a_name_nobody_declares_are_reported()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(
            """{"settings":{"game.gone":{"intensity":1}}}"""));

        await Assert.That(switches.Unknown).IsEquivalentTo(["game.gone"]);
    }

    /// <summary>Re-reading <c>engine.json</c> is a live change: a feature that read its settings
    /// once hears that they moved.</summary>
    [Test]
    public async Task replacing_settings_announces_the_new_ones()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(Document));
        switches.Declare(new FeatureDefinition("game.weather", true));
        var announced = new List<float>();
        switches.SettingsChanged += (_, settings) =>
            announced.Add(settings.Read(GameJson.Default.WeatherSettings).Intensity);

        switches.Apply(EngineConfiguration.Read("""{"settings":{"game.weather":{"intensity":0.9}}}"""));
        // The same text again: nothing moved, nothing announced.
        switches.Apply(EngineConfiguration.Read("""{"settings":{"game.weather":{"intensity":0.9}}}"""));

        await Assert.That(announced).IsEquivalentTo([0.9f]);
        await Assert.That(switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings).Intensity)
            .IsEqualTo(0.9f);
    }

    /// <summary>Wholesale, not deep-merged — a later file that means to change one field writes
    /// the object it wants. Anything else has no answer for "which layer owns element 3".</summary>
    [Test]
    public async Task a_later_layer_replaces_a_feature_s_settings_whole()
    {
        var merged = EngineConfiguration.Read(Document)
            .Merge(EngineConfiguration.Read("""{"settings":{"game.weather":{"kind":"snow"}}}"""));
        var switches = new FeatureSwitches(merged);

        var settings = switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings);

        await Assert.That(settings.Kind).IsEqualTo("snow");
        await Assert.That(settings.Intensity).IsEqualTo(1f); // the earlier layer's 0.6 is gone, not merged
    }

    [Test]
    public async Task a_layer_that_says_nothing_about_settings_leaves_them_alone()
    {
        var merged = EngineConfiguration.Read(Document)
            .Merge(EngineConfiguration.Read("""{"features":{"game.weather":false}}"""));
        var switches = new FeatureSwitches(merged);

        await Assert.That(switches.IsEnabled(s_weather)).IsFalse();
        await Assert.That(switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings).Intensity)
            .IsEqualTo(0.6f);
    }

    /// <summary>The nested-name trap the two sections exist to keep closed, and the message that
    /// says where the settings actually go.</summary>
    [Test]
    public async Task an_object_under_features_still_points_at_the_settings_section()
    {
        await Assert.That(() => EngineConfiguration.Read("""{"features":{"game":{"weather":false}}}"""))
            .Throws<FormatException>().WithMessageContaining("\"settings\"");
    }

    [Test]
    public async Task a_scalar_under_settings_is_refused_by_name()
    {
        await Assert.That(() => EngineConfiguration.Read("""{"settings":{"game.weather":true}}"""))
            .Throws<FormatException>().WithMessageContaining("game.weather");
    }

    /// <summary>A settings object that does not fit the record names the FEATURE, because that is
    /// how the file spells the thing that has to be fixed.</summary>
    [Test]
    public async Task settings_that_do_not_fit_the_type_name_the_feature()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(
            """{"settings":{"game.weather":{"intensity":"a lot"}}}"""));

        await Assert.That(() => switches.SettingsFor(s_weather).Read(GameJson.Default.WeatherSettings))
            .Throws<FormatException>().WithMessageContaining("game.weather");
    }

    /// <summary>The escape hatch for a game whose settings are not a record.</summary>
    [Test]
    public async Task raw_hands_back_what_the_file_said()
    {
        var switches = new FeatureSwitches(EngineConfiguration.Read(
            """{"settings":{"game.weather":{"intensity":0.5}}}"""));

        await Assert.That(switches.SettingsFor(s_weather).Raw).Contains("\"intensity\"");
        await Assert.That(FeatureSettings.None.Raw).IsEmpty();
    }
}

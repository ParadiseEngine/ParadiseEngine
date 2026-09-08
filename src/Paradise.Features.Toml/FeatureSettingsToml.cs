using System;
using Tomlyn;
using Tomlyn.Serialization;

namespace Paradise.Features;

/// <summary>Binds what a feature was configured with to the game's own settings type.</summary>
public static class FeatureSettingsToml
{
    /// <summary>Binds settings to T, using its defaults when the feature has no settings.</summary>
    /// <remarks>
    /// Supply the caller's source-generated context for AOT and trimming. Use camelCase naming
    /// and get/set properties so omitted keys retain their initializers.
    /// <code>
    /// public sealed record WeatherSettings
    /// {
    ///     public float Intensity { get; set; } = 1f;              // set, NOT init
    ///     public float WindMetresPerSecond { get; set; } = 2f;
    /// }
    ///
    /// [TomlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    /// [TomlSerializable(typeof(WeatherSettings))]
    /// internal sealed partial class GameToml : TomlSerializerContext;
    ///
    /// var weather = switches.SettingsFor(Weather.Id).Read&lt;WeatherSettings&gt;(GameToml.Default);
    /// </code></remarks>
    /// <exception cref="FormatException">Settings do not fit T; the message names the feature.</exception>
    public static T Read<T>(this FeatureSettings settings, TomlSerializerContext context) where T : new()
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        if (settings.IsEmpty) return new T();
        try
        {
            return TomlSerializer.Deserialize<T>(settings.Text, context) ?? new T();
        }
        catch (TomlException error)
        {
            throw new FormatException(
                $"The settings for '{settings.Name}' do not fit {typeof(T).Name}: {error.Message}", error);
        }
    }
}
